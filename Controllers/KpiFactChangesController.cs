// shortened preamble for brevity (using directives remain unchanged)
using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using KPIMonitor.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Controllers
{
    public class KpiFactChangesController : Controller
    {
        private readonly IKpiFactChangeService _svc;
        private readonly IKpiAccessService _acl;
        private readonly AppDbContext _db;
        private readonly global::IAdminAuthorizer _admin;
        private readonly IKpiFactChangeBatchService _batches;
        private readonly ILogger<KpiFactChangesController> _log;
        private readonly IEmailSender _email;
        private readonly IEmployeeDirectory _dir;

        private const string InboxUrl = "http://kpimonitor.badea.local/KpiFactChanges/Inbox";
        private const string LogoUrl = "https://kpimonitor.badea.local/images/logo-en.png";

        public KpiFactChangesController(
            IKpiFactChangeService svc,
            IKpiAccessService acl,
            AppDbContext db,
            global::IAdminAuthorizer admin,
            IKpiFactChangeBatchService batches,
            ILogger<KpiFactChangesController> log,
            IEmailSender email,
            IEmployeeDirectory dir)
        {
            _svc = svc;
            _acl = acl;
            _db = db;
            _admin = admin;
            _batches = batches;
            _log = log;
            _email = email;
            _dir = dir;
        }

        // -------------------------------------------------------
        // Helper: build professional HTML email
        // -------------------------------------------------------
        private static string BuildHtmlEmail(string title, string body)
        {
            return $@"
<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8' />
<style>
    body {{ font-family:'Segoe UI',Tahoma,Arial,sans-serif; color:#333; background-color:#f7f9fc; margin:0; padding:0; }}
    .container {{ max-width:600px; margin:40px auto; background:#fff; border:1px solid #e4e9f0; border-radius:8px; padding:24px; }}
    .logo {{ text-align:center; margin-bottom:24px; }}
    .logo img {{ height:60px; }}
    h2 {{ color:#005c99; font-weight:600; }}
    a.button {{ display:inline-block; background:#005c99; color:#fff !important; padding:10px 18px; border-radius:4px; text-decoration:none; margin-top:16px; }}
    footer {{ font-size:12px; color:#999; text-align:center; margin-top:30px; }}
</style>
</head>
<body>
    <div class='container'>
        <div class='logo'>
            <img src='{LogoUrl}' alt='BADEA Logo'/>
        </div>
        <h2>{WebUtility.HtmlEncode(title)}</h2>
        <p>{body}</p>
        <p><a href='{InboxUrl}' class='button'>Open KPI Monitor</a></p>
        <footer>
            &copy; {DateTime.UtcNow.Year} BADEA - KPI Monitor System
        </footer>
    </div>
</body>
</html>";
        }

        private static string NormalizeLogin(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string BuildEmailFromSam(string? sam)
        {
            var s = NormalizeLogin(sam);
            return string.IsNullOrWhiteSpace(s) ? "" : $"{s}@badea.org";
        }

        private string Sam() => NormalizeLogin(User?.Identity?.Name);

        private async Task<string?> MyEmpIdAsync(CancellationToken ct = default)
        {
            var sam = Sam();
            if (string.IsNullOrWhiteSpace(sam)) return null;
            var rec = await _dir.TryGetByUserIdAsync(sam, ct);
            return rec?.EmpId;
        }

        // -------------------------------------------------------
        // Resolve owner/editor info
        // -------------------------------------------------------
        private async Task<(string? ownerSam, string? ownerEmail)> ResolveOwnerAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var info = await (
                from f in _db.KpiFacts.AsNoTracking()
                join yp in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals yp.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { yp.OwnerLogin, yp.OwnerEmpId }
            ).FirstOrDefaultAsync(ct);

            string? sam = !string.IsNullOrWhiteSpace(info?.OwnerLogin)
                ? NormalizeLogin(info!.OwnerLogin)
                : null;
            var email = BuildEmailFromSam(sam);
            return (sam, email);
        }

        private async Task<(string code, string name, string full)> GetKpiHeadAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var info = await (
                from f in _db.KpiFacts.AsNoTracking()
                join k in _db.DimKpis.AsNoTracking() on f.KpiId equals k.KpiId
                join o in _db.DimObjectives.AsNoTracking() on k.ObjectiveId equals o.ObjectiveId into oj
                from o in oj.DefaultIfEmpty()
                join p in _db.DimPillars.AsNoTracking() on o.PillarId equals p.PillarId into pj
                from p in pj.DefaultIfEmpty()
                where f.KpiFactId == kpiFactId
                select new { k.KpiCode, k.KpiName, p.PillarCode, o.ObjectiveCode }
            ).FirstOrDefaultAsync(ct);

            var code = info?.KpiCode ?? $"KPI-{kpiFactId}";
            var name = info?.KpiName ?? "KPI";
            var full = $"{info?.PillarCode}.{info?.ObjectiveCode} {code} — {name}";
            return (code, name, full);
        }

        // -------------------------------------------------------
        // Email logic: Owner on submit, Editor on approval/rejection
        // -------------------------------------------------------
        private async Task SendOwnerApprovalEmailAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var (ownerSam, ownerEmail) = await ResolveOwnerAsync(kpiFactId, ct);
            if (string.IsNullOrWhiteSpace(ownerEmail)) return;

            var (_, _, fullName) = await GetKpiHeadAsync(kpiFactId, ct);
            var html = BuildHtmlEmail("KPI Approval Request",
                $"You have a new approval request for <strong>{WebUtility.HtmlEncode(fullName)}</strong>.<br><br>" +
                $"Please review it in KPI Monitor.");

            await _email.SendEmailAsync(ownerEmail, $"Approval Request — {fullName}", html);
        }

        private async Task SendEditorDecisionEmailAsync(decimal kpiFactId, string editorSam, string decision, CancellationToken ct = default)
        {
            var email = BuildEmailFromSam(editorSam);
            if (string.IsNullOrWhiteSpace(email)) return;

            var (_, _, fullName) = await GetKpiHeadAsync(kpiFactId, ct);
            var html = BuildHtmlEmail($"KPI {decision}",
                $"Your KPI <strong>{WebUtility.HtmlEncode(fullName)}</strong> has been <strong>{decision.ToLower()}</strong>.<br><br>" +
                $"You can view the details in KPI Monitor.");

            await _email.SendEmailAsync(email, $"KPI {decision} — {fullName}", html);
        }

        // -------------------------------------------------------
        // Submit (owner gets notified once)
        // -------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(decimal kpiFactId, decimal? ProposedActualValue, decimal? ProposedTargetValue,
            decimal? ProposedForecastValue, string? ProposedStatusCode)
        {
            var submittedBy = Sam();
            var change = await _svc.SubmitAsync(kpiFactId, ProposedActualValue, ProposedTargetValue, ProposedForecastValue, ProposedStatusCode, submittedBy);

            if (!_admin.IsSuperAdmin(User))
                await SendOwnerApprovalEmailAsync(kpiFactId);

            return Json(new { ok = true, status = change.ApprovalStatus, message = "Submitted successfully." });
        }

        // -------------------------------------------------------
        // Approve / Reject (editor notified)
        // -------------------------------------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(decimal changeId, CancellationToken ct = default)
        {
            var ch = await _db.KpiFactChanges.Include(x => x.KpiFact).FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId, ct);
            if (ch == null) return BadRequest();

            var reviewer = Sam();
            await _svc.ApproveAsync(changeId, reviewer);
            await SendEditorDecisionEmailAsync(ch.KpiFactId, ch.SubmittedBy ?? "", "Approved", ct);

            return Json(new { ok = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(decimal changeId, string reason, CancellationToken ct = default)
        {
            var ch = await _db.KpiFactChanges.Include(x => x.KpiFact).FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId, ct);
            if (ch == null) return BadRequest();

            var reviewer = Sam();
            await _svc.RejectAsync(changeId, reviewer, reason);
            await SendEditorDecisionEmailAsync(ch.KpiFactId, ch.SubmittedBy ?? "", "Rejected", ct);

            return Json(new { ok = true });
        }
    }
}
