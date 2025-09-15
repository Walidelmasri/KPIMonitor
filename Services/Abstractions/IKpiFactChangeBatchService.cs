using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace KPIMonitor.Services.Abstractions
{
    public interface IKpiFactChangeBatchService
    {
        Task<decimal> CreateBatchAsync(
            decimal kpiId,
            decimal planId,
            int year,
            bool isMonthly,
            int? periodMin,
            int? periodMax,
            string submittedBy,
            int createdCount,
            int skippedCount,
            CancellationToken ct = default);

        Task ApproveBatchAsync(decimal batchId, string reviewer, CancellationToken ct = default);
        Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default);
    }
}
