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
    public sealed class KpiFactChangeBatchService : IKpiFactChangeBatchService
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

        // Creates the batch row. Must match DB CHECK:
        // KPIFACTCHANGEBATCHES.Frequency IN ('monthly','quarterly')
        public async Task<decimal> CreateBatchAsync(
            decimal kpiId,
            decimal kpiYearPlanId,
            int year,
            bool monthly,
            int? periodMin,
            int? periodMax,
            string submittedBy,
            int createdCount,
            int skippedCount,
            CancellationToken ct = default)
        {
            // ✅ FIX: use the exact strings required by the constraint
            var frequency = monthly ? "monthly" : "quarterly";

            if (periodMin.HasValue && periodMax.HasValue && periodMin > periodMax)
                throw new InvalidOperationException("periodMin cannot be greater than periodMax.");

            // (Optional safety: ensure period bounds align with frequency)
            if (periodMin.HasValue || periodMax.HasValue)
            {
                if (monthly)
                {
                    if (periodMin is < 1 or > 12) throw new InvalidOperationException("Monthly periodMin must be 1..12.");
                    if (periodMax is < 1 or > 12) throw new InvalidOperationException("Monthly periodMax must be 1..12.");
                }
                else
                {
                    if (periodMin is < 1 or > 4) throw new InvalidOperationException("Quarterly periodMin must be 1..4.");
                    if (periodMax is < 1 or > 4) throw new InvalidOperationException("Quarterly periodMax must be 1..4.");
                }
            }

            var batch = new KpiFactChangeBatch
            {
                KpiId         = kpiId,
                KpiYearPlanId = kpiYearPlanId,
                Year          = year,
                Frequency     = frequency,                   // <-- important
                PeriodMin     = periodMin,
                PeriodMax     = periodMax,
                RowCount      = createdCount,
                SkippedCount  = skippedCount,
                SubmittedBy   = string.IsNullOrWhiteSpace(submittedBy) ? "editor" : submittedBy.Trim(),
                SubmittedAt   = DateTime.UtcNow,
                ApprovalStatus= "pending"                    // keep lowercase to match your controller filters
            };

            await _db.KpiFactChangeBatches.AddAsync(batch, ct);
            await _db.SaveChangesAsync(ct);

            _log.LogInformation("CreateBatch kpiId={KpiId} planId={PlanId} year={Year} freq={Freq} min={Min} max={Max} created={Created} skipped={Skipped} by={By} id={Id}",
                kpiId, kpiYearPlanId, year, frequency, periodMin, periodMax, createdCount, skippedCount, batch.SubmittedBy, batch.BatchId);

            return batch.BatchId;
        }

        public async Task ApproveBatchAsync(decimal batchId, string reviewer, CancellationToken ct = default)
        {
            var b = await _db.KpiFactChangeBatches
                    .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b == null) throw new InvalidOperationException("Batch not found.");

            b.ApprovalStatus = "approved";
            b.ReviewedBy = string.IsNullOrWhiteSpace(reviewer) ? "owner" : reviewer.Trim();
            b.ReviewedAt = DateTime.UtcNow;

            var children = await _db.KpiFactChanges
                            .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                            .ToListAsync(ct);

            foreach (var ch in children)
            {
                ch.ApprovalStatus = "approved";
                ch.ReviewedBy     = b.ReviewedBy;
                ch.ReviewedAt     = b.ReviewedAt;

                var f = await _db.KpiFacts.FirstOrDefaultAsync(x => x.KpiFactId == ch.KpiFactId, ct);
                if (f != null)
                {
                    if (ch.ProposedActualValue.HasValue)   f.ActualValue   = ch.ProposedActualValue;
                    if (ch.ProposedTargetValue.HasValue)   f.TargetValue   = ch.ProposedTargetValue;
                    if (ch.ProposedForecastValue.HasValue) f.ForecastValue = ch.ProposedForecastValue;
                    if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) f.StatusCode = ch.ProposedStatusCode;
                }
            }

            await _db.SaveChangesAsync(ct);
        }

        public async Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Reject reason is required.");

            var b = await _db.KpiFactChangeBatches
                    .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b == null) throw new InvalidOperationException("Batch not found.");

            b.ApprovalStatus = "rejected";
            b.RejectReason   = reason.Trim();
            b.ReviewedBy     = string.IsNullOrWhiteSpace(reviewer) ? "owner" : reviewer.Trim();
            b.ReviewedAt     = DateTime.UtcNow;

            var children = await _db.KpiFactChanges
                            .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                            .ToListAsync(ct);

            foreach (var ch in children)
            {
                ch.ApprovalStatus = "rejected";
                ch.ReviewedBy     = b.ReviewedBy;
                ch.ReviewedAt     = b.ReviewedAt;
                ch.RejectReason   = b.RejectReason;
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
