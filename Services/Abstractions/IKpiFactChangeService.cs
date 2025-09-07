using System.Threading.Tasks;
using KPIMonitor.Models;

namespace KPIMonitor.Services.Abstractions
{
    public interface IKpiFactChangeService
    {
        Task<bool> HasPendingAsync(decimal kpiFactId);

        /// <summary>Creates a pending change; throws if one already pending.</summary>
        Task<KpiFactChange> SubmitAsync(
            decimal kpiFactId,
            decimal? actual, decimal? target, decimal? forecast,
            string? statusCode,
            string submittedBy);

        /// <summary>Applies the change into KpiFacts and marks it approved.</summary>
        Task ApproveAsync(decimal changeId, string reviewer);

        /// <summary>Marks the change rejected (reason required).</summary>
        Task RejectAsync(decimal changeId, string reviewer, string reason);
    }
}