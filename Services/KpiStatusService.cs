using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Services
{
    /// <summary>
    /// Status engine: uses plan TargetDirection (no guessing),
    /// evaluates status per period, and writes it to KPIFacts.StatusCode.
    /// Implements your "look-ahead (k+1)" rule.
    /// </summary>
    public sealed class KpiStatusService : IKpiStatusService
    {
        private readonly AppDbContext _db;

        public KpiStatusService(AppDbContext db) => _db = db;

        /// <summary>
        /// Return the plan's explicit direction (1 = Ascending, -1 = Descending).
        /// </summary>
        public async Task<TrendDirection> InferDirectionAsync(
            decimal kpiId,
            decimal kpiYearPlanId,
            int year,
            CancellationToken ct = default)
        {
            var dir = await _db.KpiYearPlans
                .Where(p => p.KpiYearPlanId == kpiYearPlanId)
                .Select(p => (int?)p.TargetDirection)
                .FirstOrDefaultAsync(ct);

            if (dir is 1)  return TrendDirection.Ascending;
            if (dir is -1) return TrendDirection.Descending;

            // You said it will always be set; fail loudly if not.
            throw new InvalidOperationException("Year plan TargetDirection must be 1 or -1.");
        }

        public bool IsDue(DimPeriod period, DateTime nowUtc)
        {
            if (period == null) return false;

            // Determine the period end (prefer DB EndDate; otherwise compute)
            DateTime periodEndUtc;
            if (period.EndDate.HasValue)
            {
                // Treat EndDate as UTC timestamp
                periodEndUtc = DateTime.SpecifyKind(period.EndDate.Value, DateTimeKind.Utc);
            }
            else if (period.MonthNum.HasValue)
            {
                int y = period.Year, m = period.MonthNum.Value;
                int d = DateTime.DaysInMonth(y, m);
                periodEndUtc = new DateTime(y, m, d, 23, 59, 59, DateTimeKind.Utc);
            }
            else if (period.QuarterNum.HasValue)
            {
                int y = period.Year, q = period.QuarterNum.Value;
                int m = q * 3;
                int d = DateTime.DaysInMonth(y, m);
                periodEndUtc = new DateTime(y, m, d, 23, 59, 59, DateTimeKind.Utc);
            }
            else
            {
                int y = period.Year;
                periodEndUtc = new DateTime(y, 12, 31, 23, 59, 59, DateTimeKind.Utc);
            }

            // “Due” ONE MONTH after the period end
            var dueAt = periodEndUtc.AddMonths(1);
            return nowUtc >= dueAt;
        }

        /// <summary>
        /// SAME-PERIOD logic only.
        /// Order: Actual vs Target → Actual vs Forecast → DataMissing (if due & no actual) → NeedsAttention (if actual present) → null (not due & no actual).
        /// </summary>
        public string? Evaluate(
            decimal? actual,
            decimal? target,
            decimal? forecast,
            bool isDue,
            TrendDirection direction,
            decimal tolerance = 0.0001m)
        {
            static bool Meets(decimal lhs, decimal rhs, TrendDirection d, decimal tol)
                => d == TrendDirection.Ascending ? lhs + tol >= rhs
                                                 : lhs - tol <= rhs;

            // no actual recorded
            if (!actual.HasValue)
                return isDue ? StatusCodes.DataMissing : null;

            // A vs T
            if (target.HasValue && Meets(actual.Value, target.Value, direction, tolerance))
                return StatusCodes.Ok;

            // A vs F
            if (forecast.HasValue && Meets(actual.Value, forecast.Value, direction, tolerance))
                return StatusCodes.CatchingUp;

            // actual is present but misses both
            return StatusCodes.NeedsAttention;
        }

        public async Task<string> ComputeAndSetAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var fact = await _db.KpiFacts
                .Include(f => f.Period)
                .FirstOrDefaultAsync(f => f.KpiFactId == kpiFactId, ct);

            if (fact == null || fact.Period == null)
                throw new InvalidOperationException("KPI fact not found or missing period.");

            var period = fact.Period;
            var now    = DateTime.UtcNow;
            var isDue  = IsDue(period, now);

            // Direction from plan field (required to be 1 or -1)
            var dir = await InferDirectionAsync(fact.KpiId, fact.KpiYearPlanId, period.Year, ct);

            // 1) Same-period evaluation
            var same = Evaluate(fact.ActualValue, fact.TargetValue, fact.ForecastValue, isDue, dir);
            string? decided = same;

            // 2) Look-ahead (k+1) rule only if Actual exists and same is null/red
            if (fact.ActualValue.HasValue && (same == null || same == StatusCodes.NeedsAttention))
            {
                var next = await GetNextSameGranularityAsync(fact.KpiYearPlanId, period.Year, period, ct);

                if (next == null)
                {
                    decided = StatusCodes.NeedsAttention; // no k+1 to catch up
                }
                else
                {
                    var (tNext, fNext) = next.Value;

                    if (!tNext.HasValue)
                        decided = StatusCodes.DataMissing;   // rule: missing T(k+1) => blue
                    else if (fNext.HasValue && Meets(fNext.Value, tNext.Value, dir))
                        decided = StatusCodes.CatchingUp;    // rule: F(k+1) meets T(k+1) => orange
                    else
                        decided = StatusCodes.NeedsAttention; // otherwise red
                }
            }

            var effective = decided ?? (fact.StatusCode ?? "");
            if (decided != null &&
                !string.Equals(decided, fact.StatusCode, StringComparison.OrdinalIgnoreCase))
            {
                fact.StatusCode = decided;
                await _db.SaveChangesAsync(ct);
            }

            return effective;

            // local helper (direction-aware)
            static bool Meets(decimal lhs, decimal rhs, TrendDirection d, decimal tol = 0.0001m)
                => d == TrendDirection.Ascending ? lhs + tol >= rhs : lhs - tol <= rhs;
        }

        /// <summary>
        /// Returns (Target, Forecast) of the next period (k+1) in the same plan+year and
        /// with the same granularity as <paramref name="currentPeriod"/>; null if none exists.
        /// Uses only approved data (KPIFACTS), never pending.
        /// </summary>
        private async Task<(decimal? Target, decimal? Forecast)?> GetNextSameGranularityAsync(
            decimal kpiYearPlanId,
            int year,
            DimPeriod currentPeriod,
            CancellationToken ct)
        {
            var query = _db.KpiFacts.AsNoTracking()
                .Include(f => f.Period)
                .Where(f => f.KpiYearPlanId == kpiYearPlanId
                         && f.IsActive == 1
                         && f.Period != null
                         && f.Period.Year == year);

            if (currentPeriod.MonthNum.HasValue)
            {
                query = query.Where(f => f.Period!.MonthNum.HasValue &&
                                         f.Period.StartDate > currentPeriod.StartDate);
            }
            else if (currentPeriod.QuarterNum.HasValue)
            {
                query = query.Where(f => f.Period!.QuarterNum.HasValue &&
                                         f.Period.StartDate > currentPeriod.StartDate);
            }
            else
            {
                query = query.Where(f => f.Period!.StartDate > currentPeriod.StartDate);
            }

            var next = await query
                .OrderBy(f => f.Period!.StartDate)
                .Select(f => new { f.TargetValue, f.ForecastValue })
                .FirstOrDefaultAsync(ct);

            return next == null ? null : (next.TargetValue, next.ForecastValue);
        }
    }
}
