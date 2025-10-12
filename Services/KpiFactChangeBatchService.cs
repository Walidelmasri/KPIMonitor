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
        private readonly IEmailSender _email;
        private readonly IEmployeeDirectory _dir;

        // Use HTTP so images render on intranet mail clients
        private const string InboxUrl = "http://kpimonitor.badea.local/kpimonitor/KpiFactChanges/Inbox";
        private const string LogoUrl  = "http://kpimonitor.badea.local/kpimonitor/images/logo-en.png";

        public KpiFactChangeBatchService(
            AppDbContext db,
            ILogger<KpiFactChangeBatchService> log,
            IEmailSender email,
            IEmployeeDirectory dir)
        {
            _db = db;
            _log = log;
            _email = email;
            _dir = dir;
        }

        private static string HtmlEmail(string title, string bodyHtml)
        {
            string esc(string s) => System.Net.WebUtility.HtmlEncode(s);
            return $@"
<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8'/>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'/>
  <title>{esc(title)}</title>
  <style>
    body {{ font-family:-apple-system, Segoe UI, Roboto, Arial, sans-serif; background:#f6f7fb; margin:0; padding:0; }}
    .container {{ max-width:640px; margin:32px auto; background:#fff; border-radius:12px; box-shadow:0 8px 24px rgba(0,0,0,0.08); overflow:hidden; }}
    .brand {{ background:#0d6efd10; padding:16px 24px; display:flex; gap:12px; align-items:center; }}
    .brand img {{ height:36px; }}
    h1 {{ margin:0; font-size:18px; font-weight:700; color:#0d3757; }}
    .content {{ padding:24px; color:#111; line-height:1.6; }}
    .btn {{ display:inline-block; padding:10px 16px; border-radius:10px; border:1px solid #0d6efd; text-decoration:none; }}
    .muted {{ color:#777; font-size:12px; }}
  </style>
</head>
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
</body>
</html>";
        }

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
            // Map EXACTLY as your DB CHECK expects
            // frequency: 'M' or 'Q'
            var frequency = monthly ? "M" : "Q";

            if (periodMin.HasValue && periodMax.HasValue && periodMin > periodMax)
                throw new InvalidOperationException("periodMin cannot be greater than periodMax.");

            // Common DB checks this satisfies:
            // - frequency IN ('M','Q')
            // - approval_status IN ('pending','approved','rejected')  (your code queries lowercase)
            // - counts >= 0
            // - (optional) period bounds (controller already supplies 1..12 or 1..4)
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
                SubmittedBy   = string.IsNullOrWhiteSpace(submittedBy) ? "editor" : submittedBy.Trim(),
                SubmittedAt   = DateTime.UtcNow,
                ApprovalStatus= "pending"   // <- lowercase to match your controller filters
            };

            await _db.KpiFactChangeBatches.AddAsync(batch, ct);
            await _db.SaveChangesAsync(ct);

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

            // Apply children (same behavior as before—no email here; single-change emails handled in single-change paths)
            var kids = await _db.KpiFactChanges
                        .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                        .ToListAsync(ct);

            foreach (var ch in kids)
            {
                ch.ApprovalStatus = "approved";
                ch.ReviewedBy     = b.ReviewedBy;
                ch.ReviewedAt     = b.ReviewedAt;
                // Apply proposed values to the fact row
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

            // (No batch-wide email here—controller sends the single HTML email at submit time, and
            // your existing single-change approve/reject emails remain unchanged.)
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

            // Propagate to children
            var kids = await _db.KpiFactChanges
                        .Where(c => c.BatchId == batchId && c.ApprovalStatus == "pending")
                        .ToListAsync(ct);

            foreach (var ch in kids)
            {
                ch.ApprovalStatus = "rejected";
                ch.ReviewedBy     = b.ReviewedBy;
                ch.ReviewedAt     = b.ReviewedAt;
                ch.RejectReason   = reason.Trim();
            }

            await _db.SaveChangesAsync(ct);
        }
    }
}
