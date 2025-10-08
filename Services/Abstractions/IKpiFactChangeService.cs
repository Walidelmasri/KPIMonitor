using System.Threading.Tasks;
using KPIMonitor.Models;

namespace KPIMonitor.Services.Abstractions
{
    public interface IKpiFactChangeService
    {
        Task<bool> HasPendingAsync(decimal kpiFactId);

        Task<KpiFactChange> SubmitAsync(
            decimal kpiFactId,
            decimal? actual, decimal? target, decimal? forecast,
            string? statusCode,
            string submittedBy,
            decimal? batchId = null);

        // leave logic identical; just allow caller to suppress emails during batch loops
        Task ApproveAsync(decimal changeId, string reviewer, bool suppressEmail = false);
        Task RejectAsync(decimal changeId, string reviewer, string reason, bool suppressEmail = false);
    }
}
