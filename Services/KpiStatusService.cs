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
    /// Status engine: infers plan direction per (KPI, Plan, Year),
    /// evaluates status per period, and writes it to KPIFacts.StatusCode.
    /// </summary>
    public sealed class KpiStatusService : IKpiStatusService
    {
        private readonly AppDbContext _db;

        // Per-operation in-memory cache to avoid repeated direction queries
        private readonly Dictionary<(decimal KpiId, decimal PlanId, int Year), TrendDirection> _dirCache
            = new();

        public KpiStatusService(AppDbContext db) => _db = db;

        public async Task<TrendDirection> InferDirectionAsync(
            decimal kpiId,
            decimal kpiYearPlanId,
            int year,
            CancellationToken ct = default)
        {
            var key = (kpiId, kpiYearPlanId, year);
            if (_dirCache.TryGetValue(key, out var cached)) return cached;

            // Targets across the year for this plan, ordered by real chronology
            var targets = await _db.KpiFacts.AsNoTracking()
                .Include(f => f.Period)
                .Where(f => f.KpiYearPlanId == kpiYearPlanId
                         && f.IsActive == 1
                         && f.Period != null
                         && f.Period.Year == year)
                .OrderBy(f => f.Period!.StartDate)
                .Select(f => f.TargetValue)
                .ToListAsync(ct);

            decimal? first = targets.FirstOrDefault(v => v.HasValue);
            decimal? last  = targets.LastOrDefault(v => v.HasValue);

            TrendDirection dir;
            if (first.HasValue && last.HasValue)
            {
                dir = (last.Value >= first.Value) ? TrendDirection.Ascending : TrendDirection.Descending;
            }
            else
            {
                // not enough data to tell: default ascending (conservative)
                dir = TrendDirection.Ascending;
            }

            _dirCache[key] = dir;
            return dir;
        }

        public bool IsDue(DimPeriod period, DateTime nowUtc)
        {
            if (period == null) return false;

            // Reuse your existing window logic so "due" == "editable actuals"
            if (period.MonthNum.HasValue)
            {
                var mw = PeriodEditPolicy.ComputeMonthlyWindow(period.Year, nowUtc);
                return mw.ActualMonths.Contains(period.MonthNum.Value);
            }
            if (period.QuarterNum.HasValue)
            {
                var qw = PeriodEditPolicy.ComputeQuarterlyWindow(period.Year, nowUtc);
                return qw.ActualQuarters.Contains(period.QuarterNum.Value);
            }

            // Year-only rows (rare in your model): due once year has ended
            return period.EndDate <= nowUtc;
        }

        public string? Evaluate(
            decimal? actual,
            decimal? target,
            decimal? forecast,
            bool isDue,
            TrendDirection direction,
            decimal tolerance = 0.0001m)
        {
            // 1) Actual vs Target (direction-aware)
            if (actual.HasValue && target.HasValue)
            {
                if (direction == TrendDirection.Ascending)
                {
                    if (actual.Value + tolerance >= target.Value) return StatusCodes.Ok;
                }
                else
                {
                    if (actual.Value - tolerance <= target.Value) return StatusCodes.Ok;
                }
            }

            // 2) Else Actual vs Forecast (direction-aware)
            if (actual.HasValue && forecast.HasValue)
            {
                if (direction == TrendDirection.Ascending)
                {
                    if (actual.Value + tolerance >= forecast.Value) return StatusCodes.CatchingUp;
                }
                else
                {
                    if (actual.Value - tolerance <= forecast.Value) return StatusCodes.CatchingUp;
                }
            }

            // 3) If Actual is missing and the period is due/overdue → Data Missing
            if (!actual.HasValue && isDue)
                return StatusCodes.DataMissing;

            // 4) Otherwise, if Actual exists but didn't meet T/F → Needs Attention
            //    Or Actual is missing but not due yet → leave as-is (null).
            if (actual.HasValue)
                return StatusCodes.NeedsAttention;

            // Not due yet + no actual → don't overwrite whatever is already stored.
            return null;
        }

        public async Task<string> ComputeAndSetAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var fact = await _db.KpiFacts
                .Include(f => f.Period)
                .FirstOrDefaultAsync(f => f.KpiFactId == kpiFactId, ct);

            if (fact == null || fact.Period == null)
                throw new InvalidOperationException("KPI fact not found or missing period.");

            var period = fact.Period;
            var isDue  = IsDue(period, DateTime.UtcNow);

            var direction = await InferDirectionAsync(
                fact.KpiId,
                fact.KpiYearPlanId,
                period.Year,
                ct);

            var newStatus = Evaluate(
                fact.ActualValue,
                fact.TargetValue,
                fact.ForecastValue,
                isDue,
                direction);

            // If null → keep whatever is already there
            var effective = newStatus ?? (fact.StatusCode ?? "");

            if (newStatus != null && !string.Equals(newStatus, fact.StatusCode, StringComparison.OrdinalIgnoreCase))
            {
                fact.StatusCode = newStatus;
                await _db.SaveChangesAsync(ct);
            }

            return effective;
        }
    }
}
