using System.Threading.Tasks;

namespace KPIMonitor.Services.Abstractions
{
    public interface IKpiFactChangeService
    {
        Task<bool> HasPendingAsync(decimal kpiFactId);

        // NOTE: notifyOwner controls whether the Owner “pending approval” email is sent here.
        // - Single submit  : notifyOwner = true,  batchId = null
        // - Batch children : notifyOwner = false, batchId = (the batch id)
        Task<KPIMonitor.Models.KpiFactChange> SubmitAsync(
            decimal kpiFactId,
            decimal? actual,
            decimal? target,
            decimal? forecast,
            string? statusCode,
            string submittedBy,
            bool notifyOwner,
            decimal? batchId = null
        );

        // Editor emails (approved/rejected) remain unchanged
        Task ApproveAsync(decimal changeId, string reviewer, bool suppressEmail = false);
        Task RejectAsync(decimal changeId, string reviewer, string reason, bool suppressEmail = false);
    }
}
