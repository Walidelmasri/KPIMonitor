using System.Threading;
using System.Threading.Tasks;
using KPIMonitor.Models;

namespace KPIMonitor.Services.Abstractions
{
    /// <summary>
    /// Direction of "better" (target reaching) for a KPI plan in a given year.
    /// </summary>
    public enum TrendDirection
    {
        Ascending = 1,   // higher is better
        Descending = -1  // lower is better
    }

    public interface IKpiStatusService
    {
        /// <summary>
        /// Infer plan direction (ascending/descending) for a KPI & year based on
        /// first vs last non-null target in that year. Defaults to Ascending.
        /// </summary>
        Task<TrendDirection> InferDirectionAsync(
            decimal kpiId,
            decimal kpiYearPlanId,
            int year,
            CancellationToken ct = default);

        /// <summary>
        /// True when the period is due/overdue for actuals (i.e., after grace),
        /// using the same window logic you already use for editability.
        /// </summary>
        bool IsDue(DimPeriod period, System.DateTime nowUtc);

        /// <summary>
        /// Evaluate a status using the strict order:
        /// 1) Actual vs Target; 2) else Actual vs Forecast; 3) else NeedsAttention;
        /// If Actual is null and the period is due/overdue → DataMissing.
        /// Returns null to indicate "leave as-is" (e.g., Actual null but period not due).
        /// </summary>
        string? Evaluate(
            decimal? actual,
            decimal? target,
            decimal? forecast,
            bool isDue,
            TrendDirection direction,
            decimal tolerance = 0.0001m);

        /// <summary>
        /// Compute and persist the status for a single fact row (by id).
        /// Respects the 'due' window and plan direction. Returns the effective status after the call.
        /// </summary>
        Task<string> ComputeAndSetAsync(decimal kpiFactId, CancellationToken ct = default);
        /// <summary>
        /// Recomputes and persists StatusCode for all facts in a given plan + year,
        /// in chronological order so k+1 logic sees consistent values.
        /// </summary>
        Task RecomputePlanYearAsync(decimal kpiYearPlanId, int year, CancellationToken ct = default);
    
    }
}
