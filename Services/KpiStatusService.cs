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
    /// Status engine:
    /// - Uses explicit plan TargetDirection (1 asc, -1 desc).
    /// - Same-period: Actual vs Target only (NO Actual vs Forecast).
    /// - Look-ahead (k+1): ONLY Forecast(k+1) vs Target(k+1).
    /// </summary>
    public sealed class KpiStatusService : IKpiStatusService
    {
        private readonly AppDbContext _db;
        public KpiStatusService(AppDbContext db) => _db = db;

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
            throw new InvalidOperationException("Year plan TargetDirection must be 1 or -1.");
        }

        public bool IsDue(DimPeriod period, DateTime nowUtc)
        {
            if (period == null) return false;

            DateTime periodEndUtc;
            if (period.EndDate.HasValue)
            {
                periodEndUtc = DateTime.SpecifyKind(period.EndDate.Value, DateTimeKind.Utc);
            }
            else if (period.MonthNum.HasValue)
            {
                int d = DateTime.DaysInMonth(period.Year, period.MonthNum.Value);
                periodEndUtc = new DateTime(period.Year, period.MonthNum.Value, d, 23, 59, 59, DateTimeKind.Utc);
            }
            else if (period.QuarterNum.HasValue)
            {
                int m = period.QuarterNum.Value * 3;
                int d = DateTime.DaysInMonth(period.Year, m);
                periodEndUtc = new DateTime(period.Year, m, d, 23, 59, 59, DateTimeKind.Utc);
            }
            else
            {
                periodEndUtc = new DateTime(period.Year, 12, 31, 23, 59, 59, DateTimeKind.Utc);
            }

            return nowUtc >= periodEndUtc.AddMonths(1);
        }

        /// <summary>
        /// Same-period only:
        ///   - No Actual → null if not due; DataMissing if due.
        ///   - Actual present → Ok if meets Target; else NeedsAttention.
        /// </summary>
        public string? Evaluate(
            decimal? actual,
            decimal? target,
            decimal? forecast,   // intentionally unused for same-period decision
            bool isDue,
            TrendDirection direction,
            decimal tolerance = 0.0001m)
        {
            if (!actual.HasValue)
                return isDue ? StatusCodes.DataMissing : (string?)null;

            if (target.HasValue && Meets(actual.Value, target.Value, direction, tolerance))
                return StatusCodes.Ok;

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
            var dir = await InferDirectionAsync(fact.KpiId, fact.KpiYearPlanId, period.Year, ct);
            var isDue = IsDue(period, DateTime.UtcNow);

            // 1) Same-period decision (no A vs F here)
            var same = Evaluate(fact.ActualValue, fact.TargetValue, fact.ForecastValue, isDue, dir);
            string? decided = same;

            // 2) Look-ahead ONLY if Actual exists and not already Ok
            if (fact.ActualValue.HasValue && same != StatusCodes.Ok)
            {
                var next = await GetNextSameGranularityAsync(fact.KpiYearPlanId, period.Year, period, ct);
                if (next is null)
                {
                    // No next period → keep same decision; if same was null (shouldn't happen with Actual present), force red
                    decided ??= StatusCodes.NeedsAttention;
                }
                else
                {
                    var (tNext, fNext) = next.Value;

                    if (!tNext.HasValue)
                    {
                        // Missing T(k+1) → DataMissing (blue)
                        decided = StatusCodes.DataMissing;
                    }
                    else if (fNext.HasValue && Meets(fNext.Value, tNext.Value, dir))
                    {
                        // ONLY when Forecast(k+1) meets/exceeds (or <= for descending) Target(k+1)
                        decided = StatusCodes.CatchingUp;
                    }
                    else
                    {
                        // Has next, but either no Forecast(k+1) or it doesn't meet Target(k+1)
                        decided = StatusCodes.NeedsAttention;
                    }
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
        }

        /// <summary>
        /// Get (Target, Forecast) for the next period of the same granularity within the same year/plan.
        /// </summary>
        private async Task<(decimal? Target, decimal? Forecast)?> GetNextSameGranularityAsync(
            decimal kpiYearPlanId,
            int year,
            DimPeriod currentPeriod,
            CancellationToken ct)
        {
            var q = _db.KpiFacts.AsNoTracking()
                .Include(f => f.Period)
                .Where(f => f.KpiYearPlanId == kpiYearPlanId
                         && f.IsActive == 1
                         && f.Period != null
                         && f.Period.Year == year);

            if (currentPeriod.MonthNum.HasValue)
                q = q.Where(f => f.Period!.MonthNum.HasValue && f.Period.StartDate > currentPeriod.StartDate);
            else if (currentPeriod.QuarterNum.HasValue)
                q = q.Where(f => f.Period!.QuarterNum.HasValue && f.Period.StartDate > currentPeriod.StartDate);
            else
                q = q.Where(f => f.Period!.StartDate > currentPeriod.StartDate);

            var next = await q.OrderBy(f => f.Period!.StartDate)
                              .Select(f => new { f.TargetValue, f.ForecastValue })
                              .FirstOrDefaultAsync(ct);

            return next == null ? null : (next.TargetValue, next.ForecastValue);
        }

        private static bool Meets(decimal lhs, decimal rhs, TrendDirection d, decimal tol = 0.0001m)
            => d == TrendDirection.Ascending
                ? lhs + tol >= rhs
                : lhs - tol <= rhs;
    }
}
