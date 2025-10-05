using System;
using System.Collections.Generic;
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
    /// - Look-ahead (k+1): ONLY Forecast(k+1) vs Target(k+1) — and only when Actual exists AND missed the target.
    /// - If no Actual: keep current status until due; once due → DataMissing.
    /// - Forecast in the SAME period is never considered if Actual exists (forecast is "dead" then).
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
        ///   - Actual present → Ok if meets Target; else NeedsAttention. (No Actual vs Forecast here.)
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

        /// <summary>
        /// Compute and persist StatusCode for a single fact.
        /// </summary>
        public async Task<string> ComputeAndSetAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var fact = await _db.KpiFacts
                .Include(f => f.Period)
                .FirstOrDefaultAsync(f => f.KpiFactId == kpiFactId, ct);

            if (fact == null || fact.Period == null)
                throw new InvalidOperationException("KPI fact not found or missing period.");

            var now   = DateTime.UtcNow;
            var isDue = IsDue(fact.Period, now);
            var dir   = await InferDirectionAsync(fact.KpiId, fact.KpiYearPlanId, fact.Period.Year, ct);

            // 1) Same-period decision (no A vs F)
            var same = Evaluate(fact.ActualValue, fact.TargetValue, fact.ForecastValue, isDue, dir);
            string? decided = same;

            // 2) Look-ahead rule — ONLY if Actual exists and not already OK
            if (fact.ActualValue.HasValue && same != StatusCodes.Ok)
            {
                var next = await GetNextSameGranularityAsync(fact.KpiYearPlanId, fact.Period.Year, fact.Period, ct);

                if (next is null)
                {
                    decided ??= StatusCodes.NeedsAttention; // nothing to rescue it
                }
                else
                {
                    var (tNext, fNext) = next.Value;

                    if (!tNext.HasValue)
                    {
                        decided = StatusCodes.DataMissing; // missing next target → blue
                    }
                    else if (fNext.HasValue && Meets(fNext.Value, tNext.Value, dir))
                    {
                        decided = StatusCodes.CatchingUp;  // only F(k+1) vs T(k+1)
                    }
                    else
                    {
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
        /// Recompute an entire plan-year AFTER you finish saving facts (single edit, inline modal, batch, etc.).
        /// One DB save at the end if any row changed.
        /// </summary>
        public async Task RecomputePlanYearAsync(decimal kpiYearPlanId, int year, CancellationToken ct = default)
        {
            // Load facts + periods for this plan-year
            var facts = await _db.KpiFacts
                .Include(f => f.Period)
                .Where(f => f.KpiYearPlanId == kpiYearPlanId
                         && f.IsActive == 1
                         && f.Period != null
                         && f.Period.Year == year)
                .OrderBy(f => f.Period!.StartDate)
                .ToListAsync(ct);

            if (facts.Count == 0) return;

            var dir = await InferDirectionAsync(facts[0].KpiId, kpiYearPlanId, year, ct);
            var now = DateTime.UtcNow;

            bool anyChange = false;

            for (int i = 0; i < facts.Count; i++)
            {
                var f = facts[i];
                var isDue = IsDue(f.Period!, now);

                // SAME-PERIOD decision
                var same = Evaluate(f.ActualValue, f.TargetValue, f.ForecastValue, isDue, dir);
                string? decided = same;

                // LOOK-AHEAD only if Actual exists and not OK
                if (f.ActualValue.HasValue && same != StatusCodes.Ok)
                {
                    // next fact in same granularity (we already ordered by StartDate)
                    var next = GetNextFromLoadedList(facts, i);

                    if (next is null)
                    {
                        decided ??= StatusCodes.NeedsAttention;
                    }
                    else
                    {
                        var (tNext, fNext) = next.Value;
                        if (!tNext.HasValue)
                            decided = StatusCodes.DataMissing;
                        else if (fNext.HasValue && Meets(fNext.Value, tNext.Value, dir))
                            decided = StatusCodes.CatchingUp;
                        else
                            decided = StatusCodes.NeedsAttention;
                    }
                }

                // No Actual: keep previous until due; if due → DataMissing
                if (!f.ActualValue.HasValue)
                {
                    if (isDue)
                        decided = StatusCodes.DataMissing;
                    else
                        decided = null; // leave as-is
                }

                if (decided != null &&
                    !string.Equals(decided, f.StatusCode, StringComparison.OrdinalIgnoreCase))
                {
                    f.StatusCode = decided;
                    anyChange = true;
                }
            }

            if (anyChange)
                await _db.SaveChangesAsync(ct);
        }

        private static (decimal? Target, decimal? Forecast)? GetNextFromLoadedList(
            List<KpiFact> orderedFacts, int currentIndex)
        {
            if (currentIndex + 1 >= orderedFacts.Count) return null;
            var nf = orderedFacts[currentIndex + 1];
            return (nf.TargetValue, nf.ForecastValue);
        }

        /// <summary>
        /// Fallback when recomputing a single fact needs DB to find (k+1).
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
                         && f.Period.Year == year
                         && f.Period!.StartDate > currentPeriod.StartDate);

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
