using System;
using System.Linq;
using System.Net;
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
        private readonly IEmailSender _email;

        // Keep http so images render in intranet mail clients
        private const string InboxUrl = "http://kpimonitor.badea.local/kpimonitor/KpiFactChanges/Inbox";
        private const string LogoUrl  = "http://kpimonitor.badea.local/kpimonitor/images/logo-en.png";

        public KpiFactChangeBatchService(
            AppDbContext db,
            ILogger<KpiFactChangeBatchService> log,
            IEmailSender email)
        {
            _db = db;
            _log = log;
            _email = email;
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
            var frequency = monthly ? "monthly" : "quarterly";

            if (periodMin.HasValue && periodMax.HasValue && periodMin > periodMax)
                throw new InvalidOperationException("periodMin cannot be greater than periodMax.");

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
                Frequency     = frequency,
                PeriodMin     = periodMin,
                PeriodMax     = periodMax,
                RowCount      = createdCount,
                SkippedCount  = skippedCount,
                SubmittedBy   = string.IsNullOrWhiteSpace(submittedBy) ? "editor" : NormalizeSam(submittedBy),
                SubmittedAt   = DateTime.UtcNow,
                ApprovalStatus= "pending"
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
            b.ReviewedBy = string.IsNullOrWhiteSpace(reviewer) ? "owner" : NormalizeSam(reviewer);
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

            // ---- ONE email to the submitting editor (summary) ----
            try
            {
                var editorEmail = BuildEmailFromSam(b.SubmittedBy);
                if (!string.IsNullOrWhiteSpace(editorEmail))
                {
                    var (kpiCode, kpiName, pillar, objective) = await GetKpiHeadAsync(b.KpiId, ct);
                    var kpiText = $"{(pillar ?? "")}.{(objective ?? "")} {(kpiCode ?? "")} — {(kpiName ?? "-")}";
                    var perText = (b.PeriodMin.HasValue && b.PeriodMax.HasValue) ? $"{b.PeriodMin}–{b.PeriodMax}" : "—";
                    var subject = "Your KPI batch was approved";

                    var bodyHtml = $@"
<p>Your submitted batch for <em>{WebUtility.HtmlEncode(kpiText)}</em> has been <strong>approved</strong>.</p>
<p>Year: <strong>{b.Year}</strong> • Frequency: <strong>{(string.IsNullOrWhiteSpace(b.Frequency) ? "-" : WebUtility.HtmlEncode(b.Frequency))}</strong> • Periods: <strong>{WebUtility.HtmlEncode(perText)}</strong></p>
<p>Rows created: <strong>{b.RowCount}</strong> • Skipped: <strong>{b.SkippedCount}</strong></p>
<p>Reviewed by <strong>{WebUtility.HtmlEncode(b.ReviewedBy)}</strong> at {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC).</p>";

                    await _email.SendEmailAsync(editorEmail, subject, HtmlEmail(subject, bodyHtml));
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending batch APPROVED email to editor for batchId={BatchId}", batchId);
            }
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
            b.ReviewedBy     = string.IsNullOrWhiteSpace(reviewer) ? "owner" : NormalizeSam(reviewer);
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

            // ---- ONE email to the submitting editor (summary) ----
            try
            {
                var editorEmail = BuildEmailFromSam(b.SubmittedBy);
                if (!string.IsNullOrWhiteSpace(editorEmail))
                {
                    var (kpiCode, kpiName, pillar, objective) = await GetKpiHeadAsync(b.KpiId, ct);
                    var kpiText = $"{(pillar ?? "")}.{(objective ?? "")} {(kpiCode ?? "")} — {(kpiName ?? "-")}";
                    var perText = (b.PeriodMin.HasValue && b.PeriodMax.HasValue) ? $"{b.PeriodMin}–{b.PeriodMax}" : "—";
                    var subject = "Your KPI batch was rejected";

                    var bodyHtml = $@"
<p>Your submitted batch for <em>{WebUtility.HtmlEncode(kpiText)}</em> has been <strong>rejected</strong>.</p>
<p>Reason: {WebUtility.HtmlEncode(reason)}</p>
<p>Year: <strong>{b.Year}</strong> • Frequency: <strong>{(string.IsNullOrWhiteSpace(b.Frequency) ? "-" : WebUtility.HtmlEncode(b.Frequency))}</strong> • Periods: <strong>{WebUtility.HtmlEncode(perText)}</strong></p>
<p>Reviewed by <strong>{WebUtility.HtmlEncode(b.ReviewedBy)}</strong> at {DateTime.UtcNow:yyyy-MM-dd HH:mm} (UTC).</p>";

                    await _email.SendEmailAsync(editorEmail, subject, HtmlEmail(subject, bodyHtml));
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending batch REJECTED email to editor for batchId={BatchId}", batchId);
            }
        }

        // ---------- helpers ----------

        private static string NormalizeSam(string? raw)
        {
            var s = raw?.Trim() ?? "";
            if (s.Length == 0) return s;
            var bs = s.LastIndexOf('\\');
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string BuildEmailFromSam(string? submittedBySam)
        {
            var sam = NormalizeSam(submittedBySam);
            if (string.IsNullOrWhiteSpace(sam)) return "";
            return $"{sam}@badea.org";
        }

        private static string HtmlEmail(string title, string bodyHtml)
        {
            string esc(string v) => WebUtility.HtmlEncode(v);
            return $@"
<!DOCTYPE html>
<html><head><meta charset='UTF-8'/><meta name='viewport' content='width=device-width, initial-scale=1.0'/>
<title>{esc(title)}</title>
<style>
body{{font-family:-apple-system,Segoe UI,Roboto,Arial,sans-serif;background:#f6f7fb;margin:0;padding:0}}
.container{{max-width:640px;margin:32px auto;background:#fff;border-radius:12px;box-shadow:0 8px 24px rgba(0,0,0,.08);overflow:hidden}}
.brand{{background:#0d6efd10;padding:16px 24px;display:flex;gap:12px;align-items:center}}
.brand img{{height:36px}}h1{{margin:0;font-size:18px;font-weight:700;color:#0d3757}}
.content{{padding:24px;color:#111;line-height:1.6}}
.btn{{display:inline-block;padding:10px 16px;border-radius:10px;border:1px solid #0d6efd;text-decoration:none}}
.muted{{color:#777;font-size:12px}}
</style></head>
<body>
  <div class='container'>
    <div class='brand'>
      <img src='{LogoUrl}' alt='BADEA Logo'/>
      <h1>BADEA KPI Monitor</h1>
    </div>
    <div class='content'>
      <p style='margin-top:0'><strong>{esc(title)}</strong></p>
      {bodyHtml}
      <p style='margin:18px 0'><a class='btn' href='{InboxUrl}'>Open Approvals</a></p>
      <p class='muted'>This is an automated message.</p>
    </div>
  </div>
</body></html>";
        }

        private async Task<(string? kpiCode, string? kpiName, string? pillar, string? objective)> GetKpiHeadAsync(decimal kpiId, CancellationToken ct)
        {
            var h = await _db.DimKpis.AsNoTracking()
                .Where(x => x.KpiId == kpiId)
                .Select(x => new
                {
                    x.KpiCode,
                    x.KpiName,
                    Pillar = x.Pillar != null ? x.Pillar.PillarCode : null,
                    Obj    = x.Objective != null ? x.Objective.ObjectiveCode : null
                })
                .FirstOrDefaultAsync(ct);

            return (h?.KpiCode, h?.KpiName, h?.Pillar, h?.Obj);
        }
    }
}
