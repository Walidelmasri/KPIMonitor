using System;
using System.Linq;
using System.Threading.Tasks;
using System.Net;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Services
{
    public class KpiFactChangeService : IKpiFactChangeService
    {
        private readonly AppDbContext _db;
        private readonly IKpiStatusService _status;
        private readonly IEmployeeDirectory _dir;
        private readonly IEmailSender _email;
        private readonly ILogger<KpiFactChangeService> _log;

        // ===== HTML email helpers for consistency =====
        private const string InboxUrl_Service = "http://kpimonitor.badea.local/kpimonitor/KpiFactChanges/Inbox";
        private const string LogoUrl_Service  = "https://kpimonitor.badea.local/kpimonitor/images/logo-en.png";

        private static string HtmlEmailService(string title, string bodyHtml)
        {
            return $@"
<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8'/>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'/>
  <title>{WebUtility.HtmlEncode(title)}</title>
  <style>
    body {{ font-family: -apple-system, Segoe UI, Roboto, Arial, sans-serif; background:#f6f7fb; margin:0; padding:0; }}
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
      <img src='{LogoUrl_Service}' alt='BADEA Logo'/>
      <h1>BADEA KPI Monitor</h1>
    </div>
    <div class='content'>
      <p style='margin-top:0'><strong>{WebUtility.HtmlEncode(title)}</strong></p>
      {bodyHtml}
      <p style='margin:18px 0'><a class='btn' href='{InboxUrl_Service}'>Open Approvals</a></p>
      <p class='muted'>This is an automated message.</p>
    </div>
  </div>
</body>
</html>";
        }

        // ====================== EMAIL TEMPLATES (single item) ======================
        private static string OwnerPendingSingle_Subject() => "KPI change pending approval";
        private static string OwnerPendingSingle_Body(string kpiCode, string kpiName, string editorSam) =>
            $"A change was submitted for KPI {kpiCode} — {kpiName} by {editorSam}. Please review it in KPI Monitor.";

        private static string EditorApproved_Subject() => "KPI change approved";
        private static string EditorApproved_Body(string kpiCode, string kpiName) =>
            $"Your submitted change for KPI {kpiCode} — {kpiName} was approved.";

        private static string EditorRejected_Subject() => "KPI change rejected";
        private static string EditorRejected_Body(string kpiCode, string kpiName, string reason) =>
            $"Your submitted change for KPI {kpiCode} — {kpiName} was rejected. Reason: {reason}.";

        public KpiFactChangeService(
            AppDbContext db,
            IKpiStatusService status,
            IEmployeeDirectory dir,
            IEmailSender email,
            ILogger<KpiFactChangeService> log)
        {
            _db = db;
            _status = status;
            _dir = dir;
            _email = email;
            _log = log;
        }

        private static string NormalizeSam(string? raw)
        {
            var s = raw?.Trim() ?? "";
            if (string.IsNullOrEmpty(s)) return "";
            var bs = s.LastIndexOf('\\');
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');
            if (at > 0) s = s[..at];
            return s.Trim().ToLowerInvariant();
        }

        private string? BuildMailFromSam(string? rawSam)
        {
            var sam = NormalizeSam(rawSam);
            return string.IsNullOrWhiteSpace(sam) ? null : $"{sam}@badea.org";
        }

        private async Task<string?> ResolveMailFromOwnerAsync(string? ownerLogin, string? ownerEmpId)
        {
            if (!string.IsNullOrWhiteSpace(ownerLogin))
                return BuildMailFromSam(ownerLogin);

            if (!string.IsNullOrWhiteSpace(ownerEmpId))
            {
                var sam = await _dir.TryGetLoginByEmpIdAsync(ownerEmpId);
                if (!string.IsNullOrWhiteSpace(sam))
                    return BuildMailFromSam(sam);
            }

            return null;
        }

        public async Task<bool> HasPendingAsync(decimal kpiFactId)
        {
            return await _db.KpiFactChanges.AsNoTracking()
                .AnyAsync(c => c.KpiFactId == kpiFactId && c.ApprovalStatus == "pending");
        }

        public async Task<KpiFactChange> SubmitAsync(
            decimal kpiFactId,
            decimal? actual, decimal? target, decimal? forecast,
            string? statusCode,
            string submittedBy,
            decimal? batchId = null)
        {
            if (await HasPendingAsync(kpiFactId))
                throw new InvalidOperationException("A change is already pending for this KPI fact.");

            var factExists = await _db.KpiFacts
                .AsNoTracking()
                .AnyAsync(f => f.KpiFactId == kpiFactId && f.IsActive == 1);
            if (!factExists)
                throw new InvalidOperationException("KPI fact not found or inactive.");

            var change = new KpiFactChange
            {
                KpiFactId = kpiFactId,
                ProposedActualValue = actual,
                ProposedTargetValue = target,
                ProposedForecastValue = forecast,
                ProposedStatusCode = string.IsNullOrWhiteSpace(statusCode) ? null : statusCode.Trim(),
                SubmittedBy = NormalizeSam(submittedBy),
                SubmittedAt = DateTime.UtcNow,
                ApprovalStatus = "pending",
                BatchId = batchId
            };

            _db.KpiFactChanges.Add(change);
            await _db.SaveChangesAsync();

            // Owner/editor info
            var who = await (
                from f in _db.KpiFacts.AsNoTracking()
                join p in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals p.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { p.OwnerEmpId, p.EditorEmpId, p.OwnerLogin, p.EditorLogin, p.KpiYearPlanId, PeriodId = f.PeriodId, KpiId = f.KpiId }
            ).FirstOrDefaultAsync();

            var ownerEmp = who?.OwnerEmpId?.Trim();
            var editorEmp = who?.EditorEmpId?.Trim();

            // Auto-approve when owner==editor (same person)
            if (!string.IsNullOrWhiteSpace(ownerEmp) &&
                !string.IsNullOrWhiteSpace(editorEmp) &&
                string.Equals(ownerEmp, editorEmp, StringComparison.Ordinal))
            {
                var fact = await _db.KpiFacts.FirstAsync(x => x.KpiFactId == kpiFactId);

                if (change.ProposedActualValue.HasValue) fact.ActualValue = change.ProposedActualValue.Value;
                if (change.ProposedTargetValue.HasValue) fact.TargetValue = change.ProposedTargetValue.Value;
                if (change.ProposedForecastValue.HasValue) fact.ForecastValue = change.ProposedForecastValue.Value;
                if (!string.IsNullOrWhiteSpace(change.ProposedStatusCode)) fact.StatusCode = change.ProposedStatusCode;

                change.ApprovalStatus = "approved";
                change.ReviewedAt = DateTime.UtcNow;
                change.ReviewedBy = ownerEmp;

                await _db.SaveChangesAsync();

                await _status.ComputeAndSetAsync(kpiFactId);

                var factPeriodYear = await _db.DimPeriods
                    .Where(p => p.PeriodId == fact.PeriodId)
                    .Select(p => p.Year)
                    .FirstAsync();
                await _status.RecomputePlanYearAsync(fact.KpiYearPlanId, factPeriodYear);

                return change;
            }

            // Not auto-approved → if this was a "single" submit (no batch), notify owner.
            if (batchId == null)
            {
                try
                {
                    // Resolve owner email
                    var ownerMail = await ResolveMailFromOwnerAsync(who?.OwnerLogin, who?.OwnerEmpId);
                    if (!string.IsNullOrWhiteSpace(ownerMail))
                    {
                        var planInfo = await _db.DimKpis
                            .Where(k => k.KpiId == who!.KpiId)
                            .Select(k => new { k.KpiCode, k.KpiName })
                            .FirstOrDefaultAsync();

                        var editorUser = NormalizeSam(submittedBy);
                        var subject = OwnerPendingSingle_Subject();
                        var raw = OwnerPendingSingle_Body(planInfo?.KpiCode ?? "KPI", planInfo?.KpiName ?? "-", editorUser);
                        var html = HtmlEmailService(subject, $"<p>{WebUtility.HtmlEncode(raw)}</p>");
                        await _email.SendEmailAsync(ownerMail!, subject, html);
                    }
                    else
                    {
                        _log.LogWarning("Owner email could not be resolved (single submit). KpiFactId={Kfi}", kpiFactId);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending PENDING email (single).");
                }
            }

            return change;
        }

        public async Task ApproveAsync(decimal changeId, string reviewer, bool suppressEmail = false)
        {
            var ch = await _db.KpiFactChanges
                .Include(x => x.KpiFact)
                .FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (ch.KpiFact == null) throw new InvalidOperationException("KPI fact missing.");

            // Only approve pending
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                return;

            var fact = ch.KpiFact;

            if (ch.ProposedActualValue.HasValue) fact.ActualValue = ch.ProposedActualValue.Value;
            if (ch.ProposedTargetValue.HasValue) fact.TargetValue = ch.ProposedTargetValue.Value;
            if (ch.ProposedForecastValue.HasValue) fact.ForecastValue = ch.ProposedForecastValue.Value;
            if (!string.IsNullOrWhiteSpace(ch.ProposedStatusCode)) fact.StatusCode = ch.ProposedStatusCode;

            ch.ApprovalStatus = "approved";
            ch.ReviewedBy = NormalizeSam(reviewer);
            ch.ReviewedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            await _status.ComputeAndSetAsync(fact.KpiFactId);

            var approvedFact = await _db.KpiFacts.AsNoTracking()
                .Where(f => f.KpiFactId == fact.KpiFactId)
                .Select(f => new { f.KpiYearPlanId, f.PeriodId, f.KpiId })
                .FirstAsync();

            var approvedYear = await _db.DimPeriods
                .Where(p => p.PeriodId == approvedFact.PeriodId)
                .Select(p => p.Year)
                .FirstAsync();

            await _status.RecomputePlanYearAsync(approvedFact.KpiYearPlanId, approvedYear);

            if (!suppressEmail)
            {
                try
                {
                    var plan = await _db.KpiYearPlans.AsNoTracking()
                        .Where(p => p.KpiYearPlanId == approvedFact.KpiYearPlanId)
                        .Select(p => new { p.EditorLogin })
                        .FirstOrDefaultAsync();

                    var editorSam = !string.IsNullOrWhiteSpace(plan?.EditorLogin)
                        ? plan!.EditorLogin
                        : ch.SubmittedBy;

                    var to = BuildMailFromSam(editorSam);
                    if (!string.IsNullOrWhiteSpace(to))
                    {
                        var k = await _db.DimKpis.AsNoTracking()
                            .Where(x => x.KpiId == approvedFact.KpiId)
                            .Select(x => new { x.KpiCode, x.KpiName })
                            .FirstOrDefaultAsync();

                        var subject = EditorApproved_Subject();
                        var raw = EditorApproved_Body(k?.KpiCode ?? "KPI", k?.KpiName ?? "-");
                        var html = HtmlEmailService(subject, $"<p>{WebUtility.HtmlEncode(raw)}</p>");
                        await _email.SendEmailAsync(to!, subject, html);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending APPROVED email (single).");
                }
            }
        }

        public async Task RejectAsync(decimal changeId, string reviewer, string reason, bool suppressEmail = false)
        {
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException("Reject reason is required.");

            var ch = await _db.KpiFactChanges
                .Include(x => x.KpiFact)
                .FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId);

            if (ch == null) throw new InvalidOperationException("Change request not found.");
            if (ch.KpiFact == null) throw new InvalidOperationException("KPI fact missing.");

            // Only reject pending
            if (!string.Equals(ch.ApprovalStatus, "pending", StringComparison.OrdinalIgnoreCase))
                return;

            ch.ApprovalStatus = "rejected";
            ch.ReviewedBy = NormalizeSam(reviewer);
            ch.ReviewedAt = DateTime.UtcNow;
            ch.RejectReason = reason.Trim();

            await _db.SaveChangesAsync();

            if (!suppressEmail)
            {
                try
                {
                    var plan = await _db.KpiFacts.AsNoTracking()
                        .Where(f => f.KpiFactId == ch.KpiFactId)
                        .Join(_db.KpiYearPlans.AsNoTracking(), f => f.KpiYearPlanId, p => p.KpiYearPlanId,
                            (f, p) => new { f.KpiId, p.EditorLogin })
                        .FirstOrDefaultAsync();

                    var to = BuildMailFromSam(!string.IsNullOrWhiteSpace(plan?.EditorLogin) ? plan!.EditorLogin : ch.SubmittedBy);
                    if (!string.IsNullOrWhiteSpace(to))
                    {
                        var kpi = await _db.DimKpis.AsNoTracking()
                            .Where(x => x.KpiId == plan!.KpiId)
                            .Select(x => new { x.KpiCode, x.KpiName })
                            .FirstOrDefaultAsync();

                        var subject = EditorRejected_Subject();
                        var raw = EditorRejected_Body(kpi?.KpiCode ?? "KPI", kpi?.KpiName ?? "-", reason.Trim());
                        var html = HtmlEmailService(subject, $"<p>{WebUtility.HtmlEncode(raw)}</p>");
                        await _email.SendEmailAsync(to!, subject, html);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Failed sending REJECTED email (single).");
                }
            }
        }
    }
}
