using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Services
{
    public sealed class KpiFactChangeBatchService : IKpiFactChangeBatchService
    {
        private readonly AppDbContext _db;
        private readonly IKpiFactChangeService _detailSvc;

        public KpiFactChangeBatchService(AppDbContext db, IKpiFactChangeService detailSvc)
        {
            _db = db;
            _detailSvc = detailSvc;
        }

        public async Task<decimal> CreateBatchAsync(
            decimal kpiId,
            decimal planId,
            int year,
            bool isMonthly,
            int? periodMin,
            int? periodMax,
            string submittedBy,
            int createdCount,
            int skippedCount,
            CancellationToken ct = default)
        {
            var b = new KpiFactChangeBatch
            {
                KpiId = kpiId,
                KpiYearPlanId = planId,
                Year = year,
                PeriodMin = periodMin,
                PeriodMax = periodMax,
                RowCount = createdCount,
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
            var childIds = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .Select(c => c.KpiFactChangeId)
                .ToListAsync(ct);

            foreach (var id in childIds)
                await _detailSvc.ApproveAsync(id, reviewer, suppressEmail: true);

            var b = await _db.KpiFactChangeBatches.FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b != null)
            {
                b.ApprovalStatus = "approved";
                b.ReviewedBy = reviewer;
                b.ReviewedAt = DateTime.UtcNow;
                b.RejectReason = null;
                await _db.SaveChangesAsync(ct);
            }
        }

        public async Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default)
        {
            var childIds = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .Select(c => c.KpiFactChangeId)
                .ToListAsync(ct);

            foreach (var id in childIds)
                await _detailSvc.RejectAsync(id, reviewer, reason, suppressEmail: true);

            var b = await _db.KpiFactChangeBatches.FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b != null)
            {
                b.ApprovalStatus = "rejected";
                b.ReviewedBy = reviewer;
                b.ReviewedAt = DateTime.UtcNow;
                b.RejectReason = reason;
                await _db.SaveChangesAsync(ct);
            }
        }
    }
}
