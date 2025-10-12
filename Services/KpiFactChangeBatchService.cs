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
            var b = new KpiFactChangeBatch
            {
                KpiId = kpiId,
                KpiYearPlanId = kpiYearPlanId,
                Year = year,
                Frequency = monthly ? "M" : "Q",
                PeriodMin = periodMin,
                PeriodMax = periodMax,
                RowCount = rowCount,
                SkippedCount = skippedCount,
                SubmittedBy = submittedBy,
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending"
            };
            _db.KpiFactChangeBatches.Add(b);
            await _db.SaveChangesAsync(ct);
            return b.BatchId;
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

            foreach (var id in children)
            {
                // suppress per-row editor emails → controller sends ONE summary email
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
                // suppress per-row editor emails → controller sends ONE summary email
                await _changeSvc.RejectAsync(id, reviewer, reason, suppressEmail: true);
            }

            b.ApprovalStatus = "rejected";
            b.ReviewedBy = reviewer;
            b.ReviewedAt = DateTime.UtcNow;
            b.RejectReason = reason.Trim();
            await _db.SaveChangesAsync(ct);
        }
    }
}
