using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Services
{
    public class KpiFactChangeBatchService : IKpiFactChangeBatchService
    {
        private readonly AppDbContext _db;
        private readonly ILogger<KpiFactChangeBatchService> _log;

        public KpiFactChangeBatchService(
            AppDbContext db,
            ILogger<KpiFactChangeBatchService> log)
        {
            _db = db;
            _log = log;
        }

        public async Task<decimal> CreateBatchAsync(
            decimal kpiId,
            decimal kpiYearPlanId,
            int year,
            bool monthly,
            int? periodMin,
            int? periodMax,
            string submittedBy,
            int rowCount,
            int skippedCount,
            CancellationToken ct = default)
        {
            var b = new KpiFactChangeBatch
            {
                KpiId = kpiId,
                KpiYearPlanId = kpiYearPlanId,
                Year = year,
                Frequency = monthly ? "M" : "Q",   // keep your DB’s original codes
                PeriodMin = periodMin,
                PeriodMax = periodMax,
                RowCount = rowCount,
                SkippedCount = skippedCount,
                SubmittedBy = (submittedBy ?? "").Trim(),
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending"
            };

            _db.KpiFactChangeBatches.Add(b);
            await _db.SaveChangesAsync(ct);
            return b.BatchId;
        }

        public async Task ApproveBatchAsync(decimal batchId, string reviewer, CancellationToken ct = default)
        {
            // Approve children directly here to avoid triggering any per-row email logic elsewhere.
            var changes = await _db.KpiFactChanges
                .Include(c => c.KpiFact)
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .ToListAsync(ct);

            var now = DateTime.UtcNow;

            foreach (var c in changes)
            {
                // apply deltas to fact
                if (c.KpiFact != null)
                {
                    if (c.ProposedActualValue.HasValue)
                        c.KpiFact.ActualValue = c.ProposedActualValue;
                    if (c.ProposedTargetValue.HasValue)
                        c.KpiFact.TargetValue = c.ProposedTargetValue;
                    if (c.ProposedForecastValue.HasValue)
                        c.KpiFact.ForecastValue = c.ProposedForecastValue;
                    if (!string.IsNullOrWhiteSpace(c.ProposedStatusCode))
                        c.KpiFact.StatusCode = c.ProposedStatusCode;
                }

                c.ApprovalStatus = "approved";
                c.ReviewedBy = reviewer;
                c.ReviewedAt = now;
            }

            var b = await _db.KpiFactChangeBatches
                .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);

            if (b == null) throw new InvalidOperationException("Batch not found.");

            b.ApprovalStatus = "approved";
            b.ReviewedBy = reviewer;
            b.ReviewedAt = now;

            await _db.SaveChangesAsync(ct);
        }

        public async Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default)
        {
            var changes = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .ToListAsync(ct);

            var now = DateTime.UtcNow;

            foreach (var c in changes)
            {
                c.ApprovalStatus = "rejected";
                c.ReviewedBy = reviewer;
                c.ReviewedAt = now;
                c.RejectReason = reason?.Trim();
            }

            var b = await _db.KpiFactChangeBatches
                .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);

            if (b == null) throw new InvalidOperationException("Batch not found.");

            b.ApprovalStatus = "rejected";
            b.ReviewedBy = reviewer;
            b.ReviewedAt = now;
            b.RejectReason = reason?.Trim();

            await _db.SaveChangesAsync(ct);
        }
    }
}
