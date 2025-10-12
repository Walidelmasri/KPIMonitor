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
        private readonly IKpiFactChangeService _changeSvc;
        private readonly ILogger<KpiFactChangeBatchService> _log;

        public KpiFactChangeBatchService(
            AppDbContext db,
            IKpiFactChangeService changeSvc,
            ILogger<KpiFactChangeBatchService> log)
        {
            _db = db;
            _changeSvc = changeSvc;
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
            // NEVER nulls for NOT NULL schemas; conservative Frequency text
            var b = new KpiFactChangeBatch
            {
                KpiId = kpiId,
                KpiYearPlanId = kpiYearPlanId,
                Year = year,
                Frequency = monthly ? "monthly" : "quarterly",
                PeriodMin = periodMin ?? 0,
                PeriodMax = periodMax ?? 0,
                RowCount = rowCount,
                SkippedCount = skippedCount,
                SubmittedBy = (submittedBy ?? "").Trim(),
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending"
            };

            try
            {
                _db.KpiFactChangeBatches.Add(b);
                await _db.SaveChangesAsync(ct);
                return b.BatchId;
            }
            catch (Exception ex)
            {
                var root = ex.GetBaseException()?.Message ?? ex.Message;
                _log.LogError(ex, "CreateBatchAsync failed: {Root}", root);
                throw;
            }
        }

        public async Task ApproveBatchAsync(decimal batchId, string reviewer, CancellationToken ct = default)
        {
            var b = await _db.KpiFactChangeBatches
                .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b == null) throw new InvalidOperationException("Batch not found.");
            if (!string.Equals(b.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase)) return;

            var children = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .Select(c => c.KpiFactChangeId)
                .ToListAsync(ct);

            // Approve each child with email suppression (controller sends ONE summary)
            foreach (var id in children)
            {
                await _changeSvc.ApproveAsync(id, reviewer, suppressEmail: true);
            }

            b.ApprovalStatus = "approved";
            b.ReviewedBy = reviewer;
            b.ReviewedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        public async Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default)
        {
            var b = await _db.KpiFactChangeBatches
                .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b == null) throw new InvalidOperationException("Batch not found.");
            if (!string.Equals(b.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase)) return;

            var children = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .Select(c => c.KpiFactChangeId)
                .ToListAsync(ct);

            foreach (var id in children)
            {
                await _changeSvc.RejectAsync(id, reviewer, reason, suppressEmail: true);
            }

            b.ApprovalStatus = "rejected";
            b.ReviewedBy = reviewer;
            b.ReviewedAt = DateTime.UtcNow;
            b.RejectReason = reason?.Trim();
            await _db.SaveChangesAsync(ct);
        }
    }
}
