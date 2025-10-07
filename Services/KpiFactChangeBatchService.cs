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
        private readonly IKpiFactChangeService _detailSvc;

        // ADDED: email + logger
        private readonly IEmailSender _email;
        private readonly ILogger<KpiFactChangeBatchService> _log;

        // UPDATED: constructor injects email + logger
        public KpiFactChangeBatchService(AppDbContext db,
                                         IKpiFactChangeService detailSvc,
                                         IEmailSender email,
                                         ILogger<KpiFactChangeBatchService> log)
        {
            _db = db;
            _detailSvc = detailSvc;
            _email = email;
            _log = log;
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
                // Frequency intentionally left as-is (null or set elsewhere)
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
                await _detailSvc.ApproveAsync(id, reviewer);

            var b = await _db.KpiFactChangeBatches.FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b != null)
            {
                b.ApprovalStatus = "approved";
                b.ReviewedBy = reviewer;
                b.ReviewedAt = DateTime.UtcNow;
                b.RejectReason = null;
                await _db.SaveChangesAsync(ct);
            }

            // One email to the editor for the whole batch
            await NotifyEditor_BatchDecisionAsync(batchId, approved: true, reviewer, reason: null);
        }

        public async Task RejectBatchAsync(decimal batchId, string reviewer, string reason, CancellationToken ct = default)
        {
            var childIds = await _db.KpiFactChanges
                .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                .Select(c => c.KpiFactChangeId)
                .ToListAsync(ct);

            foreach (var id in childIds)
                await _detailSvc.RejectAsync(id, reviewer, reason);

            var b = await _db.KpiFactChangeBatches.FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b != null)
            {
                b.ApprovalStatus = "rejected";
                b.ReviewedBy = reviewer;
                b.ReviewedAt = DateTime.UtcNow;
                b.RejectReason = reason;
                await _db.SaveChangesAsync(ct);
            }

            // One email to the editor for the whole batch (with reason)
            await NotifyEditor_BatchDecisionAsync(batchId, approved: false, reviewer, reason);
        }

        // ---------- private helpers (added) ----------

        private static string NormalizeSam(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\'); if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@'); if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string EmailFromSam(string sam) => $"{sam}@badea.org";

        private const string AppRootUrl = "http://kpimonitor.badea.local/kpimonitor";

        private async Task<(string KpiLabel, int Year, string? OwnerSam, string? EditorSam)> GetBatchContextAsync(decimal batchId)
        {
            var rec = await (
                from b in _db.KpiFactChangeBatches
                join yp in _db.KpiYearPlans on b.KpiYearPlanId equals yp.KpiYearPlanId
                join k in _db.DimKpis on b.KpiId equals k.KpiId
                join per in _db.DimPeriods on yp.PeriodId equals per.PeriodId
                where b.BatchId == batchId
                select new
                {
                    per.Year,
                    OwnerSam = yp.OwnerLogin,
                    EditorSam = yp.EditorLogin,
                    Label = (k.Pillar != null ? k.Pillar.PillarCode : "") + "." +
                            (k.Objective != null ? k.Objective.ObjectiveCode : "") + " " +
                            (k.KpiCode ?? "") + " — " + (k.KpiName ?? "-")
                }
            ).FirstAsync();

            return (rec.Label, rec.Year,
                    string.IsNullOrWhiteSpace(rec.OwnerSam) ? null : NormalizeSam(rec.OwnerSam),
                    string.IsNullOrWhiteSpace(rec.EditorSam) ? null : NormalizeSam(rec.EditorSam));
        }

        private async Task NotifyEditor_BatchDecisionAsync(decimal batchId, bool approved, string reviewer, string? reason)
        {
            try
            {
                var (label, year, _, editorSamFromPlan) = await GetBatchContextAsync(batchId);

                // If plan has no editor login, fallback to any child change's SubmittedBy
                string editorSam = editorSamFromPlan ?? await _db.KpiFactChanges
                    .Where(c => c.BatchId == batchId)
                    .OrderBy(c => c.SubmittedAt)
                    .Select(c => NormalizeSam(c.SubmittedBy))
                    .FirstOrDefaultAsync() ?? "editor";

                var to = EmailFromSam(editorSam);
                var statusText = approved ? "approved" : "rejected";
                var subj = $"Your KPI changes were {statusText} – {label} (Year {year})";
                var body = approved
                    ? $@"
<p>Your submission for <strong>{label}</strong> (Year {year}) was approved by <strong>{NormalizeSam(reviewer)}</strong>.</p>
<p><a href=""{AppRootUrl}"">{AppRootUrl}</a></p>"
                    : $@"
<p>Your submission for <strong>{label}</strong> (Year {year}) was rejected by <strong>{NormalizeSam(reviewer)}</strong>.</p>
<p><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason ?? "-")}</p>
<p><a href=""{AppRootUrl}"">{AppRootUrl}</a></p>";

                await _email.SendEmailAsync(to, subj, body);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "NotifyEditor_BatchDecision failed for batch {BatchId}", batchId);
            }
        }
    }
}
