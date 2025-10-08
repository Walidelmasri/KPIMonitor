using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net;                      // WebUtility.HtmlEncode
using System.Collections.Generic;
using System.Data;                     // IDbCommand, etc.
using System.Data.Common;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;             // IKpiAccessService, IEmployeeDirectory
using KPIMonitor.Services.Abstractions; // IKpiFactChangeService, IKpiFactChangeBatchService, IEmailSender
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

        // ------------------------
        // helpers
        // ------------------------
        private static string Sam(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');             // DOMAIN\user
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');                  // user@domain
            if (at > 0) s = s[..at];
            return s.Trim();
        }
        private string Sam() => Sam(User?.Identity?.Name);

        private async Task<string?> MyEmpIdAsync(CancellationToken ct = default)
        {
            var sam = Sam();
            if (string.IsNullOrWhiteSpace(sam)) return null;
            var rec = await _dir.TryGetByUserIdAsync(sam, ct);
            return rec?.EmpId; // BADEA_ADDONS.EMPLOYEES.EMP_ID
        }

        private static string PeriodLabel(DimPeriod? p)
        {
            if (p == null) return "—";
            if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
            if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
            return p.Year.ToString();
        }

        private static string NormalizeLogin(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');                  // DOMAIN\user
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');                       // user@domain
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        private static string BuildEmailFromSam(string? sam)
        {
            var s = NormalizeLogin(sam);
            return string.IsNullOrWhiteSpace(s) ? "" : $"{s}@badea.org";
        }

        private async Task<string?> LookupUserIdByEmpIdAsync(string empId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(empId)) return null;

            var conn = _db.Database.GetDbConnection();
            if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT USERID FROM BADEA_ADDONS.EMPLOYEES WHERE EMP_ID = :p_emp";
            var p = cmd.CreateParameter();
            p.ParameterName = "p_emp";
            p.Value = empId;
            cmd.Parameters.Add(p);

            var result = await cmd.ExecuteScalarAsync(ct);
            var userId = result as string;
            userId = NormalizeLogin(userId);
            return string.IsNullOrWhiteSpace(userId) ? null : userId;
        }

        private async Task<(string? ownerSam, string? ownerEmail)> ResolveOwnerAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var info = await (
                from f in _db.KpiFacts.AsNoTracking()
                join yp in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals yp.KpiYearPlanId
                where f.KpiFactId == kpiFactId
                select new { yp.OwnerLogin, yp.OwnerEmpId }
            ).FirstOrDefaultAsync(ct);

            string? sam = null;
            if (!string.IsNullOrWhiteSpace(info?.OwnerLogin))
                sam = NormalizeLogin(info!.OwnerLogin);
            else if (!string.IsNullOrWhiteSpace(info?.OwnerEmpId))
                sam = await LookupUserIdByEmpIdAsync(info!.OwnerEmpId!, ct);

            var email = BuildEmailFromSam(sam);
            if (string.IsNullOrWhiteSpace(email)) return (null, null);
            return (sam, email);
        }

        private async Task<(string? editorSam, string? editorEmail)> ResolveEditorForChangeAsync(decimal changeId, CancellationToken ct = default)
        {
            var info = await (
                from c in _db.KpiFactChanges.AsNoTracking()
                join f in _db.KpiFacts.AsNoTracking() on c.KpiFactId equals f.KpiFactId
                join yp in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals yp.KpiYearPlanId
                where c.KpiFactChangeId == changeId
                select new { yp.EditorLogin, yp.EditorEmpId, c.SubmittedBy }
            ).FirstOrDefaultAsync(ct);

            string? sam = null;

            if (!string.IsNullOrWhiteSpace(info?.EditorLogin))
                sam = NormalizeLogin(info!.EditorLogin);
            else if (!string.IsNullOrWhiteSpace(info?.EditorEmpId))
                sam = await LookupUserIdByEmpIdAsync(info!.EditorEmpId!, ct);
            else if (!string.IsNullOrWhiteSpace(info?.SubmittedBy))
                sam = NormalizeLogin(info!.SubmittedBy);

            var email = BuildEmailFromSam(sam);
            if (string.IsNullOrWhiteSpace(email)) return (null, null);
            return (sam, email);
        }

        private async Task<(string? ownerSam, string? ownerEmail)> ResolveOwnerForPlanAsync(decimal planId, CancellationToken ct = default)
        {
            var info = await _db.KpiYearPlans.AsNoTracking()
                .Where(p => p.KpiYearPlanId == planId)
                .Select(p => new { p.OwnerLogin, p.OwnerEmpId })
                .FirstOrDefaultAsync(ct);

            string? sam = null;
            if (!string.IsNullOrWhiteSpace(info?.OwnerLogin))
                sam = NormalizeLogin(info!.OwnerLogin);
            else if (!string.IsNullOrWhiteSpace(info?.OwnerEmpId))
                sam = await LookupUserIdByEmpIdAsync(info!.OwnerEmpId!, ct);

            var email = BuildEmailFromSam(sam);
            if (string.IsNullOrWhiteSpace(email)) return (null, null);
            return (sam, email);
        }

        private async Task<(string code, string name)> GetKpiHeadAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var info = await (
                from f in _db.KpiFacts.AsNoTracking()
                join k in _db.DimKpis.AsNoTracking() on f.KpiId equals k.KpiId
                where f.KpiFactId == kpiFactId
                select new { k.KpiCode, k.KpiName }
            ).FirstOrDefaultAsync(ct);
            return (info?.KpiCode ?? $"KPI-{kpiFactId}", info?.KpiName ?? "KPI");
        }

        private async Task<(string code, string name)> GetKpiHeadByPlanAsync(decimal planId, CancellationToken ct = default)
        {
            var info = await (
                from p in _db.KpiYearPlans.AsNoTracking()
                join k in _db.DimKpis.AsNoTracking() on p.KpiId equals k.KpiId
                where p.KpiYearPlanId == planId
                select new { k.KpiCode, k.KpiName }
            ).FirstOrDefaultAsync(ct);
            return (info?.KpiCode ?? $"KPI", info?.KpiName ?? "KPI");
        }

        private async Task<string> GetPeriodTextAsync(decimal kpiFactId, CancellationToken ct = default)
        {
            var per = await (
                from f in _db.KpiFacts.AsNoTracking()
                join p in _db.DimPeriods.AsNoTracking() on f.PeriodId equals p.PeriodId
                where f.KpiFactId == kpiFactId
                select p
            ).FirstOrDefaultAsync(ct);
            return PeriodLabel(per);
        }

        // Email body builders (plain text — professional, no bold/HTML)
        private static string BuildOwnerSubmitSubject(string kpiCode) =>
            $"KPI Monitor — Approval required for {kpiCode}";

        private static string BuildOwnerSubmitBody(string kpiCode, string kpiName, string submittedBy, string periodText)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"A change has been submitted for KPI {kpiCode} — {kpiName}.");
            sb.AppendLine($"Submitted by: {submittedBy}");
            if (!string.IsNullOrWhiteSpace(periodText))
                sb.AppendLine($"Period: {periodText}");
            sb.AppendLine();
            sb.AppendLine("Please review and either approve or reject the request in KPI Monitor.");
            return sb.ToString();
        }

        private static string BuildOwnerSubmitBatchSubject(string kpiCode) =>
            $"KPI Monitor — Approval required for multiple edits to {kpiCode}";

        private static string BuildOwnerSubmitBatchBody(string kpiCode, string kpiName, string submittedBy, int year, bool monthly, int? min, int? max, int createdCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Several edits have been submitted for KPI {kpiCode} — {kpiName}.");
            sb.AppendLine($"Submitted by: {submittedBy}");
            sb.AppendLine($"Year: {year}");
            sb.AppendLine($"Frequency: {(monthly ? "Monthly" : "Quarterly")}");
            if (min.HasValue || max.HasValue)
                sb.AppendLine($"Range: {(min?.ToString() ?? "—")} to {(max?.ToString() ?? "—")}");
            sb.AppendLine($"Items awaiting review: {createdCount}");
            sb.AppendLine();
            sb.AppendLine("Please review and either approve or reject the request in KPI Monitor.");
            return sb.ToString();
        }

        private static string BuildEditorDecisionSubject(string kpiCode, bool approved) =>
            approved
                ? $"KPI Monitor — Your change for {kpiCode} was approved"
                : $"KPI Monitor — Your change for {kpiCode} was rejected";

        private static string BuildEditorDecisionBody(string kpiCode, string kpiName, bool approved, string? reason, string reviewer, string periodText)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Your change for KPI {kpiCode} — {kpiName} has been {(approved ? "approved" : "rejected")}.");
            if (!string.IsNullOrWhiteSpace(periodText))
                sb.AppendLine($"Period: {periodText}");
            sb.AppendLine($"Reviewed by: {reviewer}");
            if (!approved && !string.IsNullOrWhiteSpace(reason))
            {
                sb.AppendLine();
                sb.AppendLine("Reason:");
                sb.AppendLine(reason);
            }
            return sb.ToString();
        }

        private static string BuildEditorDecisionBatchSubject(string kpiCode, bool approved) =>
            approved
                ? $"KPI Monitor — Your submitted batch for {kpiCode} was approved"
                : $"KPI Monitor — Your submitted batch for {kpiCode} was rejected";

        private static string BuildEditorDecisionBatchBody(string kpiCode, string kpiName, bool approved, string reviewer, string? reason, int year, bool monthly, int? min, int? max)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Your submitted batch for KPI {kpiCode} — {kpiName} has been {(approved ? "approved" : "rejected")}.");
            sb.AppendLine($"Year: {year}");
            sb.AppendLine($"Frequency: {(monthly ? "Monthly" : "Quarterly")}");
            if (min.HasValue || max.HasValue)
                sb.AppendLine($"Range: {(min?.ToString() ?? "—")} to {(max?.ToString() ?? "—")}");
            sb.AppendLine($"Reviewed by: {reviewer}");
            if (!approved && !string.IsNullOrWhiteSpace(reason))
            {
                sb.AppendLine();
                sb.AppendLine("Reason:");
                sb.AppendLine(reason);
            }
            return sb.ToString();
        }

        private async Task SendOwnerSubmitEmailAsync(decimal kpiFactId, string submittedBy, CancellationToken ct = default)
        {
            try
            {
                var (ownerSam, ownerEmail) = await ResolveOwnerAsync(kpiFactId, ct);
                if (string.IsNullOrWhiteSpace(ownerEmail))
                {
                    _log.LogWarning("Owner email resolve failed for factId={FactId}. Skipping owner mail.", kpiFactId);
                    return;
                }

                var (code, name) = await GetKpiHeadAsync(kpiFactId, ct);
                var per = await GetPeriodTextAsync(kpiFactId, ct);

                var subject = BuildOwnerSubmitSubject(code);
                var body = BuildOwnerSubmitBody(code, name, submittedBy, per);

                await _email.SendEmailAsync(ownerEmail, subject, WebUtility.HtmlEncode(body));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending owner submit email for factId={FactId}", kpiFactId);
            }
        }

        private async Task SendOwnerSubmitBatchEmailAsync(decimal planId, int year, bool monthly, int? min, int? max, int createdCount, string submittedBy, CancellationToken ct = default)
        {
            try
            {
                var (ownerSam, ownerEmail) = await ResolveOwnerForPlanAsync(planId, ct);
                if (string.IsNullOrWhiteSpace(ownerEmail))
                {
                    _log.LogWarning("Owner email resolve failed for planId={PlanId}. Skipping owner batch mail.", planId);
                    return;
                }

                var (code, name) = await GetKpiHeadByPlanAsync(planId, ct);

                var subject = BuildOwnerSubmitBatchSubject(code);
                var body = BuildOwnerSubmitBatchBody(code, name, submittedBy, year, monthly, min, max, createdCount);

                await _email.SendEmailAsync(ownerEmail, subject, WebUtility.HtmlEncode(body));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending owner batch submit email for planId={PlanId}", planId);
            }
        }

        private async Task SendEditorDecisionEmailAsync(decimal changeId, bool approved, string reviewer, string? reason, CancellationToken ct = default)
        {
            try
            {
                var (editorSam, editorEmail) = await ResolveEditorForChangeAsync(changeId, ct);
                if (string.IsNullOrWhiteSpace(editorEmail))
                {
                    _log.LogWarning("Editor email resolve failed for changeId={ChangeId}. Skipping editor mail.", changeId);
                    return;
                }

                var ctx = await (
                    from c in _db.KpiFactChanges.AsNoTracking()
                    join f in _db.KpiFacts.AsNoTracking() on c.KpiFactId equals f.KpiFactId
                    join k in _db.DimKpis.AsNoTracking() on f.KpiId equals k.KpiId
                    join p in _db.DimPeriods.AsNoTracking() on f.PeriodId equals p.PeriodId
                    where c.KpiFactChangeId == changeId
                    select new { k.KpiCode, k.KpiName, Period = p }
                ).FirstOrDefaultAsync(ct);

                var code = ctx?.KpiCode ?? $"KPI";
                var name = ctx?.KpiName ?? "KPI";
                var perText = PeriodLabel(ctx?.Period);

                var subject = BuildEditorDecisionSubject(code, approved);
                var body = BuildEditorDecisionBody(code, name, approved, reason, reviewer, perText);

                await _email.SendEmailAsync(editorEmail, subject, WebUtility.HtmlEncode(body));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending editor decision email for changeId={ChangeId}", changeId);
            }
        }

        private async Task SendBatchResultEmailAsync(decimal batchId, bool approved, string reviewer, string? reason, CancellationToken ct = default)
        {
            try
            {
                var b = await _db.KpiFactChangeBatches.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
                if (b == null) return;

                // Editor = batch submitted by SAM (preferred), else plan editor
                var editorSam = NormalizeLogin(b.SubmittedBy);
                string? editorEmail = BuildEmailFromSam(editorSam);

                if (string.IsNullOrWhiteSpace(editorEmail))
                {
                    var plan = await _db.KpiYearPlans.AsNoTracking()
                        .Where(p => p.KpiYearPlanId == b.KpiYearPlanId)
                        .Select(p => new { p.EditorLogin, p.EditorEmpId })
                        .FirstOrDefaultAsync(ct);

                    if (!string.IsNullOrWhiteSpace(plan?.EditorLogin))
                        editorEmail = BuildEmailFromSam(plan!.EditorLogin);
                    else if (!string.IsNullOrWhiteSpace(plan?.EditorEmpId))
                    {
                        var uid = await LookupUserIdByEmpIdAsync(plan!.EditorEmpId!, ct);
                        editorEmail = BuildEmailFromSam(uid);
                    }
                }

                if (string.IsNullOrWhiteSpace(editorEmail))
                {
                    _log.LogWarning("Editor email resolve failed for batchId={BatchId}. Skipping editor batch mail.", batchId);
                    return;
                }

                var k = await _db.DimKpis.AsNoTracking()
                    .Where(x => x.KpiId == b.KpiId)
                    .Select(x => new { x.KpiCode, x.KpiName })
                    .FirstOrDefaultAsync(ct);

                var code = k?.KpiCode ?? "KPI";
                var name = k?.KpiName ?? "KPI";

                var subject = BuildEditorDecisionBatchSubject(code, approved);
                var body = BuildEditorDecisionBatchBody(code, name, approved, reviewer, reason, b.Year, b.Frequency?.ToLowerInvariant().Contains("month") == true || b.Frequency?.ToLowerInvariant() == "monthly", b.PeriodMin, b.PeriodMax);

                await _email.SendEmailAsync(editorEmail, subject, WebUtility.HtmlEncode(body));
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed sending editor batch decision email for batchId={BatchId}", batchId);
            }
        }

        // ------------------------
        // Small JSONs
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> HasPending(decimal kpiFactId)
        {
            var pending = await _svc.HasPendingAsync(kpiFactId);
            return Json(new { pending });
        }

        [HttpGet]
        public async Task<IActionResult> PendingCount()
        {
            if (_admin.IsAdmin(User) || _admin.IsSuperAdmin(User))
            {
                var countAll = await _db.KpiFactChanges.AsNoTracking()
                    .CountAsync(c => c.ApprovalStatus == "pending");
                return Json(new { count = countAll });
            }

            var myEmp = await MyEmpIdAsync();
            if (string.IsNullOrWhiteSpace(myEmp)) return Json(new { count = 0 });

            var count = await (
                from c in _db.KpiFactChanges.AsNoTracking()
                join f in _db.KpiFacts.AsNoTracking() on c.KpiFactId equals f.KpiFactId
                join yp in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals yp.KpiYearPlanId
                where c.ApprovalStatus == "pending"
                      && yp.OwnerEmpId != null
                      && yp.OwnerEmpId == myEmp
                select c.KpiFactChangeId
            ).CountAsync();

            return Json(new { count });
        }

        // ------------------------
        // Submit / Approve / Reject (single change)
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Submit(
            decimal kpiFactId,
            decimal? ProposedActualValue,
            decimal? ProposedTargetValue,
            decimal? ProposedForecastValue,
            string? ProposedStatusCode)
        {
            try
            {
                var submittedBy = Sam();
                if (string.IsNullOrWhiteSpace(submittedBy)) submittedBy = "editor";

                var change = await _svc.SubmitAsync(
                    kpiFactId,
                    ProposedActualValue, ProposedTargetValue, ProposedForecastValue,
                    ProposedStatusCode,
                    submittedBy);

                // Auto-approve if SuperAdmin (still no emails in this path to keep it simple)
                if (_admin.IsSuperAdmin(User))
                {
                    await _svc.ApproveAsync(change.KpiFactChangeId, submittedBy);
                    change.ApprovalStatus = "approved"; // keep response consistent
                }

                var msg = string.Equals(change.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)
                          ? "Saved & auto-approved."
                          : "Submitted for approval.";

                // Email owner ONLY if it’s pending (no email for auto-approved)
                if (!string.Equals(change.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase))
                {
                    await SendOwnerSubmitEmailAsync(kpiFactId, submittedBy);
                }

                return Json(new
                {
                    ok = true,
                    changeId = change.KpiFactChangeId,
                    status = change.ApprovalStatus,
                    message = msg
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Approve(decimal changeId)
        {
            try
            {
                var ch = await _db.KpiFactChanges
                    .Include(x => x.KpiFact)
                    .FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId);

                if (ch == null) return BadRequest(new { ok = false, error = "Change request not found." });
                if (ch.KpiFact == null) return BadRequest(new { ok = false, error = "KPI fact missing." });

                if (!await IsOwnerOrAdminForChangeAsync(changeId))
                    return StatusCode(403, new { ok = false, error = "Only the KPI Owner (or Admin) can approve this change." });

                var reviewer = Sam();
                if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "owner";

                await _svc.ApproveAsync(changeId, reviewer);

                // Notify editor (approved)
                await SendEditorDecisionEmailAsync(changeId, approved: true, reviewer: reviewer, reason: null);

                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Reject(decimal changeId, string reason)
        {
            try
            {
                var ch = await _db.KpiFactChanges
                    .Include(x => x.KpiFact)
                    .FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId);

                if (ch == null) return BadRequest(new { ok = false, error = "Change request not found." });
                if (ch.KpiFact == null) return BadRequest(new { ok = false, error = "KPI fact missing." });

                if (!await IsOwnerOrAdminForChangeAsync(changeId))
                    return StatusCode(403, new { ok = false, error = "Only the KPI Owner (or Admin) can reject this change." });

                if (string.IsNullOrWhiteSpace(reason))
                    return BadRequest(new { ok = false, error = "Reject reason is required." });

                var reviewer = Sam();
                if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "owner";

                await _svc.RejectAsync(changeId, reviewer, reason);

                // Notify editor (rejected)
                await SendEditorDecisionEmailAsync(changeId, approved: false, reviewer: reviewer, reason: reason);

                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        // ------------------------
        // Inbox page + HTML fragments
        // ------------------------
        [HttpGet]
        public IActionResult Inbox() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListHtml(string? status = "pending", string? modeOverride = null, CancellationToken ct = default)
        {
            var s = (status ?? "pending").Trim().ToLowerInvariant();
            if (s != "pending" && s != "approved" && s != "rejected") s = "pending";

            var isAdmin = _admin.IsAdmin(User) || _admin.IsSuperAdmin(User);
            var myEmp = await MyEmpIdAsync(ct);
            var mySam = Sam();
            var mySamUp = mySam.ToUpperInvariant();

            var isOwnerSomewhere = !string.IsNullOrWhiteSpace(myEmp) &&
                                   await _db.KpiYearPlans.AsNoTracking()
                                       .AnyAsync(p => p.OwnerEmpId == myEmp, ct);

            var mode = (isAdmin || isOwnerSomewhere) ? "owner" : "editor";
            var forced = (modeOverride ?? Request?.Query["mode"].FirstOrDefault())?.Trim().ToLowerInvariant();
            if (forced == "editor") mode = "editor";
            else if (forced == "owner" && (isAdmin || isOwnerSomewhere)) mode = "owner";

            var q = _db.KpiFactChanges.AsNoTracking()
                                      .Where(c => c.ApprovalStatus == s);

            if (mode == "owner" && !isAdmin)
            {
                q = q.Where(c =>
                    (from f in _db.KpiFacts
                     join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                     where f.KpiFactId == c.KpiFactId
                           && yp.OwnerEmpId != null
                           && yp.OwnerEmpId == myEmp
                     select 1).Any()
                );
            }
            else if (mode == "editor")
            {
                if (string.IsNullOrWhiteSpace(myEmp))
                {
                    q = q.Where(c => c.SubmittedBy != null && c.SubmittedBy.ToUpper() == mySamUp);
                }
                else
                {
                    q = q.Where(c =>
                        (from f in _db.KpiFacts
                         join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                         where f.KpiFactId == c.KpiFactId
                               && yp.EditorEmpId != null
                               && yp.EditorEmpId == myEmp
                         select 1).Any()
                    );
                }
            }

            var items = await q
                .OrderByDescending(c => c.SubmittedAt)
                .Select(c => new
                {
                    c.KpiFactChangeId,
                    c.KpiFactId,
                    c.ProposedActualValue,
                    c.ProposedTargetValue,
                    c.ProposedForecastValue,
                    c.ProposedStatusCode,
                    c.SubmittedBy,
                    c.SubmittedAt,
                    c.ApprovalStatus,
                    c.RejectReason,
                    c.ReviewedBy,
                    c.ReviewedAt
                })
                .ToListAsync(ct);

            var factIds = items.Select(i => i.KpiFactId).Distinct().ToList();

            var head = await _db.KpiFacts
                .AsNoTracking()
                .Where(f => factIds.Contains(f.KpiFactId))
                .Select(f => new
                {
                    f.KpiFactId,
                    f.ActualValue,
                    f.TargetValue,
                    f.ForecastValue,
                    f.StatusCode,
                    PerYear = f.Period != null ? (int?)f.Period.Year : null,
                    PerMonth = f.Period != null ? f.Period.MonthNum : null,
                    PerQuarter = f.Period != null ? f.Period.QuarterNum : null,
                    PillarCode = f.Kpi != null && f.Kpi.Pillar != null ? f.Kpi.Pillar.PillarCode : null,
                    ObjectiveCode = f.Kpi != null && f.Kpi.Objective != null ? f.Kpi.Objective.ObjectiveCode : null,
                    KpiCode = f.Kpi != null ? f.Kpi.KpiCode : null,
                    KpiName = f.Kpi != null ? f.Kpi.KpiName : null
                })
                .ToDictionaryAsync(x => x.KpiFactId, x => x, ct);

            static string H(string? s2) => WebUtility.HtmlEncode(s2 ?? "");
            static string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";
            static string LabelStatus(string code) => (code ?? "").Trim().ToLowerInvariant() switch
            {
                "conforme" => "Ok",
                "ecart" => "Needs Attention",
                "rattrapage" => "Catching Up",
                "attente" => "Data Missing",
                "" => "—",
                _ => code!
            };

            string DiffNum(string title, decimal? curV, decimal? newV)
            {
                var changed = (newV.HasValue && curV != newV);
                var cls = changed ? "appr-diff" : "text-muted";
                var cur = curV.HasValue ? curV.Value.ToString("0.###") : "—";
                var pro = newV.HasValue ? newV.Value.ToString("0.###") : "—";
                return $@"
<div class='appr-cell'>
  <div class='small text-muted'>{H(title)}</div>
  <div class='{cls}'>{H(cur)} → <strong>{H(pro)}</strong></div>
</div>";
            }

            string DiffStatus(string cur, string prop)
            {
                cur = cur?.Trim() ?? "";
                prop = prop?.Trim() ?? "";
                var changed = (!string.IsNullOrWhiteSpace(prop) &&
                               !string.Equals(cur, prop, StringComparison.OrdinalIgnoreCase));
                var cls = changed ? "appr-diff" : "text-muted";
                return $@"
<div class='appr-cell'>
  <div class='small text-muted'>Status</div>
  <div class='{cls}'>{H(LabelStatus(cur))} → <strong>{H(LabelStatus(prop))}</strong></div>
</div>";
            }

            var sb = new StringBuilder();
            if (items.Count == 0)
            {
                sb.Append("<div class='text-muted small'>No items.</div>");
            }
            else
            {
                foreach (var c in items)
                {
                    head.TryGetValue(c.KpiFactId, out var h);

                    var code = (h == null)
                        ? $"KPI {H(c.KpiFactId.ToString())}"
                        : $"{H(h.PillarCode ?? "")}.{H(h.ObjectiveCode ?? "")} {H(h.KpiCode ?? "")} — {H(h.KpiName ?? "-")}";

                    var perLabel = (h == null)
                        ? "—"
                        : (h.PerMonth.HasValue
                            ? $"{h.PerYear} — {new DateTime(h.PerYear ?? 2000, h.PerMonth ?? 1, 1):MMM}"
                            : h.PerQuarter.HasValue
                                ? $"{h.PerYear} — Q{h.PerQuarter}"
                                : $"{h.PerYear}");

                    var canAct = (mode == "owner");

                    var rowHead = $@"
<div class='d-flex justify-content-between align-items-start'>
  <div>
    <div class='fw-bold'>{code}</div>
    <div class='small text-muted'>Period: {H(perLabel)}</div>
    <div class='small text-muted'>Submitted by <strong>{H(c.SubmittedBy)}</strong> at {F(c.SubmittedAt)}</div>
  </div>
  <div class='text-end'>
    {(
        c.ApprovalStatus == "pending"
        ? (canAct
            ? $@"<div class='btn-group'>
                    <button type='button' class='btn btn-success btn-sm appr-btn' data-action='approve' data-id='{c.KpiFactChangeId}'>Approve</button>
                    <button type='button' class='btn btn-outline-danger btn-sm appr-btn' data-action='reject' data-id='{c.KpiFactChangeId}'>Reject</button>
                 </div>"
            : "<span class='badge text-bg-warning'>Pending</span>")
      : c.ApprovalStatus == "approved"
        ? "<span class='badge text-bg-success'>Approved</span>"
        : $@"<span class='badge text-bg-danger'>Rejected</span>
             <div class='small text-muted mt-1'>Reason: {H(c.RejectReason)}</div>"
      )}
  </div>
</div>";

                    var rowDiffs = $@"
<div class='row g-3 mt-2'>
  <div class='col-6 col-md-3'>{DiffNum("Actual", h?.ActualValue, c.ProposedActualValue)}</div>
  <div class='col-6 col-md-3'>{DiffNum("Target", h?.TargetValue, c.ProposedTargetValue)}</div>
  <div class='col-6 col-md-3'>{DiffNum("Forecast", h?.ForecastValue, c.ProposedForecastValue)}</div>
  <div class='col-6 col-md-3'>{DiffStatus(h?.StatusCode ?? "", c.ProposedStatusCode ?? "")}</div>
</div>";

                    sb.Append($@"
<div class='border rounded-3 bg-white p-3 mb-2'>
  {rowHead}
  {rowDiffs}
</div>");
                }
            }

            return Content(sb.ToString(), "text/html");
        }

        private async Task<bool> IsOwnerOrAdminForChangeAsync(decimal changeId)
        {
            if (_admin.IsAdmin(User) || _admin.IsSuperAdmin(User))
                return true;

            var myEmp = await MyEmpIdAsync();
            if (string.IsNullOrWhiteSpace(myEmp)) return false;

            var ownerEmpId = await (
                from c in _db.KpiFactChanges
                join f in _db.KpiFacts on c.KpiFactId equals f.KpiFactId
                join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                where c.KpiFactChangeId == changeId
                select yp.OwnerEmpId
            ).FirstOrDefaultAsync();

            return !string.IsNullOrWhiteSpace(ownerEmpId) && ownerEmpId == myEmp;
        }

        // ------------------------
        // Submit a batch (creates batch + links children)
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SubmitBatch(
            decimal kpiId,
            int year,
            bool isMonthly,
            Dictionary<int, decimal?>? Actuals,
            Dictionary<int, decimal?>? Forecasts,
            Dictionary<int, decimal?>? ActualQuarters,
            Dictionary<int, decimal?>? ForecastQuarters,
            Dictionary<int, decimal?>? Targets,
            Dictionary<int, decimal?>? TargetQuarters)
        {
            try
            {
                var traceId = HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString("n");

                var plan = await _db.KpiYearPlans
                    .Include(p => p.Period)
                    .AsNoTracking()
                    .Where(p => p.KpiId == kpiId && p.IsActive == 1 && p.Period != null)
                    .OrderByDescending(p => p.KpiYearPlanId)
                    .FirstOrDefaultAsync();

                if (plan == null || plan.Period == null)
                    return BadRequest(new { ok = false, error = "No active year plan found for this KPI.", traceId });

                if (plan.Period.Year != year)
                    return BadRequest(new { ok = false, error = $"Year mismatch. Active plan year is {plan.Period.Year}.", traceId });

                if (!await _acl.CanEditPlanAsync(plan.KpiYearPlanId, User))
                    return StatusCode(403, new { ok = false, error = "You do not have access to edit these facts.", traceId });

                var facts = await _db.KpiFacts
                    .Include(f => f.Period)
                    .Where(f => f.KpiId == kpiId
                             && f.IsActive == 1
                             && f.KpiYearPlanId == plan.KpiYearPlanId
                             && f.Period != null
                             && f.Period.Year == year)
                    .OrderBy(f => f.Period!.StartDate)
                    .ToListAsync();

                bool monthly = facts.Any(f => f.Period!.MonthNum.HasValue) || (!facts.Any() && isMonthly);

                var postedActuals  = monthly ? (Actuals ?? new Dictionary<int, decimal?>())
                                             : (ActualQuarters ?? new Dictionary<int, decimal?>());
                var postedForecast = monthly ? (Forecasts ?? new Dictionary<int, decimal?>())
                                             : (ForecastQuarters ?? new Dictionary<int, decimal?>());
                var postedTargets  = monthly ? (Targets ?? new Dictionary<int, decimal?>())
                                             : (TargetQuarters ?? new Dictionary<int, decimal?>());

                var nowUtc = DateTime.UtcNow;
                var isSuperAdmin = _admin.IsSuperAdmin(User);
                HashSet<int> editableA, editableF;
                if (monthly)
                {
                    var mw = PeriodEditPolicy.ComputeMonthlyWindow(year, nowUtc, User);
                    editableA = new HashSet<int>(mw.ActualMonths);
                    editableF = new HashSet<int>(mw.ForecastMonths);
                }
                else
                {
                    var qw = PeriodEditPolicy.ComputeQuarterlyWindow(year, nowUtc, User);
                    editableA = new HashSet<int>(qw.ActualQuarters);
                    editableF = new HashSet<int>(qw.ForecastQuarters);
                }

                var submittedBy = Sam();
                if (string.IsNullOrWhiteSpace(submittedBy)) submittedBy = "editor";

                int created = 0, skipped = 0;
                var errors = new List<object>();
                var createdIds = new List<decimal>();
                int minKey = int.MaxValue, maxKey = int.MinValue;

                foreach (var f in facts)
                {
                    int key = monthly ? (f.Period!.MonthNum ?? 0) : (f.Period!.QuarterNum ?? 0);
                    if (key == 0) { skipped++; continue; }

                    bool aProvided = postedActuals.ContainsKey(key) && postedActuals[key].HasValue;
                    bool fProvided = postedForecast.ContainsKey(key) && postedForecast[key].HasValue;
                    bool tProvided = postedTargets.ContainsKey(key) && postedTargets[key].HasValue;

                    postedActuals.TryGetValue(key, out var newA);
                    postedForecast.TryGetValue(key, out var newF);
                    postedTargets.TryGetValue(key, out var newT);

                    bool changeA = aProvided && (f.ActualValue != newA);
                    bool changeF = fProvided && (f.ForecastValue != newF);
                    bool changeT = isSuperAdmin && tProvided && (f.TargetValue != newT);

                    if (!changeA && !changeF && !changeT) { skipped++; continue; }

                    if ((changeA && !editableA.Contains(key)) || (changeF && !editableF.Contains(key)))
                    { skipped++; continue; }

                    if (await _svc.HasPendingAsync(f.KpiFactId))
                    { skipped++; continue; }

                    try
                    {
                        var change = await _svc.SubmitAsync(
                            f.KpiFactId,
                            changeA ? newA : null,   // Actual
                            changeT ? newT : null,   // Target (super-admin only)
                            changeF ? newF : null,   // Forecast
                            null,                    // Status
                            submittedBy);

                        // Auto-approve immediately for SuperAdmin (no emails here)
                        if (isSuperAdmin)
                        {
                            await _svc.ApproveAsync(change.KpiFactChangeId, submittedBy);
                        }

                        created++;
                        createdIds.Add(change.KpiFactChangeId);

                        minKey = Math.Min(minKey, key);
                        maxKey = Math.Max(maxKey, key);
                    }
                    catch (Exception ex)
                    {
                        skipped++;
                        errors.Add(new { factId = f.KpiFactId, periodKey = key, error = ex.Message });
                    }
                }

                if (created == 0 && errors.Count > 0)
                    return BadRequest(new { ok = false, created, skipped, errors, traceId });

                decimal? batchId = null;
                if (created > 0)
                {
                    int? periodMin = (minKey == int.MaxValue) ? (int?)null : minKey;
                    int? periodMax = (maxKey == int.MinValue) ? (int?)null : maxKey;

                    var newBatchId = await _batches.CreateBatchAsync(
                        kpiId,
                        plan.KpiYearPlanId,
                        year,
                        monthly,
                        periodMin,
                        periodMax,
                        submittedBy,
                        created,
                        skipped);

                    var children = await _db.KpiFactChanges
                        .Where(c => createdIds.Contains(c.KpiFactChangeId))
                        .ToListAsync();

                    foreach (var ch in children) ch.BatchId = newBatchId;
                    await _db.SaveChangesAsync();

                    batchId = newBatchId;

                    if (isSuperAdmin)
                    {
                        _log.LogInformation("SUPERADMIN_BYPASS SubmitBatch user={User} kpiId={KpiId} planId={PlanId} year={Year} monthly={Monthly} created={Created} skipped={Skipped} batchId={BatchId} traceId={TraceId}",
                            Sam(), kpiId, plan.KpiYearPlanId, year, monthly, created, skipped, batchId, traceId);
                    }

                    // Owner email (one email per batch)
                    if (!isSuperAdmin)
                    {
                        await SendOwnerSubmitBatchEmailAsync(plan.KpiYearPlanId, year, monthly, periodMin, periodMax, created, submittedBy);
                    }
                }

                return Json(new
                {
                    ok = true,
                    kpiId,
                    planId = plan.KpiYearPlanId,
                    year,
                    monthly,
                    created,
                    skipped,
                    batchId,
                    traceId,
                    superAdminBypass = isSuperAdmin
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message, traceId = HttpContext.TraceIdentifier });
            }
        }

        // ------------------------
        // Batch owner/admin check
        // ------------------------
        private async Task<bool> IsOwnerOrAdminForBatchAsync(decimal batchId, CancellationToken ct = default)
        {
            if (_admin.IsAdmin(User) || _admin.IsSuperAdmin(User))
                return true;

            var myEmp = await MyEmpIdAsync(ct);
            if (string.IsNullOrWhiteSpace(myEmp)) return false;

            var ownerEmpId = await (
                from b in _db.KpiFactChangeBatches.AsNoTracking()
                join yp in _db.KpiYearPlans.AsNoTracking() on b.KpiYearPlanId equals yp.KpiYearPlanId
                where b.BatchId == batchId
                select yp.OwnerEmpId
            ).FirstOrDefaultAsync(ct);

            return !string.IsNullOrWhiteSpace(ownerEmpId) && ownerEmpId == myEmp;
        }

        // ------------------------
        // Approve/Reject entire batch
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveBatch(decimal batchId, CancellationToken ct = default)
        {
            try
            {
                if (!await IsOwnerOrAdminForBatchAsync(batchId, ct))
                    return StatusCode(403, new { ok = false, error = "Only the KPI Owner (or Admin) can approve this batch." });

                var reviewer = Sam();
                if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "owner";

                await _batches.ApproveBatchAsync(batchId, reviewer, ct);

                // Notify editor of batch decision (approved)
                await SendBatchResultEmailAsync(batchId, approved: true, reviewer: reviewer, reason: null, ct);

                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectBatch(decimal batchId, string reason, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(reason))
                    return BadRequest(new { ok = false, error = "Reject reason is required." });

                if (!await IsOwnerOrAdminForBatchAsync(batchId, ct))
                    return StatusCode(403, new { ok = false, error = "Only the KPI Owner (or Admin) can reject this batch." });

                var reviewer = Sam();
                if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "owner";

                await _batches.RejectBatchAsync(batchId, reviewer, reason.Trim(), ct);

                // Notify editor of batch decision (rejected)
                await SendBatchResultEmailAsync(batchId, approved: false, reviewer: reviewer, reason: reason, ct);

                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        // ------------------------
        // List batches (cards)
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListBatchesHtml(string? status = "pending", string? modeOverride = null, CancellationToken ct = default)
        {
            var s = (status ?? "pending").Trim().ToLowerInvariant();
            if (s != "pending" && s != "approved" && s != "rejected") s = "pending";

            var isAdmin = _admin.IsAdmin(User) || _admin.IsSuperAdmin(User);
            var myEmp = await MyEmpIdAsync(ct);

            var isOwnerSomewhere = !string.IsNullOrWhiteSpace(myEmp) &&
                                   await _db.KpiYearPlans.AsNoTracking()
                                       .AnyAsync(p => p.OwnerEmpId == myEmp, ct);
            var mode = (isAdmin || isOwnerSomewhere) ? "owner" : "editor";
            var forced = (modeOverride ?? Request?.Query["mode"].FirstOrDefault())?.Trim().ToLowerInvariant();
            if (forced == "editor") mode = "editor";
            else if (forced == "owner" && (isAdmin || isOwnerSomewhere)) mode = "owner";

            var qb = _db.KpiFactChangeBatches.AsNoTracking()
                        .Where(b => b.ApprovalStatus == s);

            if (mode == "owner" && !isAdmin)
            {
                qb = qb.Where(b =>
                    (from p in _db.KpiYearPlans
                     where p.KpiYearPlanId == b.KpiYearPlanId
                           && p.OwnerEmpId != null
                           && p.OwnerEmpId == myEmp
                     select 1).Any());
            }
            else if (mode == "editor")
            {
                qb = qb.Where(b =>
                    (from p in _db.KpiYearPlans
                     where p.KpiYearPlanId == b.KpiYearPlanId
                           && p.EditorEmpId != null
                           && p.EditorEmpId == myEmp
                     select 1).Any());
            }

            var batches = await qb
                .OrderByDescending(b => b.SubmittedAt)
                .Select(b => new
                {
                    b.BatchId,
                    b.KpiId,
                    b.KpiYearPlanId,
                    b.Year,
                    b.Frequency,
                    b.PeriodMin,
                    b.PeriodMax,
                    b.RowCount,
                    b.SkippedCount,
                    b.SubmittedBy,
                    b.SubmittedAt,
                    b.ApprovalStatus,
                    b.ReviewedBy,
                    b.ReviewedAt,
                    b.RejectReason
                })
                .ToListAsync(ct);

            if (batches.Count == 0)
                return Content("<div class='text-muted small'>No items.</div>", "text/html");

            var kpiIds = batches.Select(b => b.KpiId).Distinct().ToList();
            var kpiHead = await _db.DimKpis
                .AsNoTracking()
                .Where(k => kpiIds.Contains(k.KpiId))
                .Select(k => new
                {
                    k.KpiId,
                    k.KpiCode,
                    k.KpiName,
                    PillarCode = k.Pillar != null ? k.Pillar.PillarCode : null,
                    ObjectiveCode = k.Objective != null ? k.Objective.ObjectiveCode : null
                })
                .ToDictionaryAsync(x => x.KpiId, x => x, ct);

            var batchIds = batches.Select(b => b.BatchId).ToList();
            var children = await _db.KpiFactChanges
                .AsNoTracking()
                .Where(c => c.BatchId != null && batchIds.Contains(c.BatchId.Value) && c.ApprovalStatus == s)
                .Select(c => new
                {
                    c.KpiFactChangeId,
                    c.BatchId,
                    c.KpiFactId,
                    c.ProposedActualValue,
                    c.ProposedForecastValue,
                    c.SubmittedBy,
                    c.SubmittedAt
                })
                .ToListAsync(ct);

            var factIds = children.Select(c => c.KpiFactId).Distinct().ToList();
            var facts = await _db.KpiFacts
                .AsNoTracking()
                .Where(f => factIds.Contains(f.KpiFactId))
                .Select(f => new
                {
                    f.KpiFactId,
                    f.ActualValue,
                    f.ForecastValue,
                    Period = f.Period
                })
                .ToDictionaryAsync(x => x.KpiFactId, x => x, ct);

            static string H(string? s2) => WebUtility.HtmlEncode(s2 ?? "");
            static string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";
            string DiffNum(decimal? curV, decimal? newV)
            {
                var changed = (newV.HasValue && curV != newV);
                var cls = changed ? "appr-diff" : "text-muted";
                var cur = curV.HasValue ? curV.Value.ToString("0.###") : "—";
                var pro = newV.HasValue ? newV.Value.ToString("0.###") : "—";
                return $"<div class='{cls}'>{H(cur)} → <strong>{H(pro)}</strong></div>";
            }

            var sb = new StringBuilder();

            foreach (var b in batches)
            {
                kpiHead.TryGetValue(b.KpiId, out var kh);
                var code = (kh == null)
                    ? $"KPI {H(b.KpiId.ToString())}"
                    : $"{H(kh.PillarCode ?? "")}.{H(kh.ObjectiveCode ?? "")} {H(kh.KpiCode ?? "")} — {H(kh.KpiName ?? "-")}";

                var rows = children.Where(c => c.BatchId == b.BatchId).ToList();

                rows.Sort((a, z) =>
                {
                    facts.TryGetValue(a.KpiFactId, out var fa);
                    facts.TryGetValue(z.KpiFactId, out var fz);
                    var pa = fa?.Period;
                    var pz = fz?.Period;

                    int ya = pa?.Year ?? 0;
                    int yz = pz?.Year ?? 0;

                    int ka = pa?.MonthNum ?? 0;
                    if (ka == 0 && pa?.QuarterNum.HasValue == true) ka = pa.QuarterNum.Value * 3;

                    int kz = pz?.MonthNum ?? 0;
                    if (kz == 0 && pz?.QuarterNum.HasValue == true) kz = pz.QuarterNum.Value * 3;

                    int cmpY = ya.CompareTo(yz);
                    if (cmpY != 0) return cmpY;

                    int cmpK = ka.CompareTo(kz);
                    if (cmpK != 0) return cmpK;

                    var da = pa?.StartDate ?? DateTime.MinValue;
                    var dz = pz?.StartDate ?? DateTime.MinValue;
                    return da.CompareTo(dz);
                });

                var canAct = (mode == "owner");
                string headerRight;
                if (b.ApprovalStatus == "pending")
                {
                    headerRight = $@"
<div class='btn-group'>
  {(canAct ? $@"<button type='button' class='btn btn-success btn-sm appr-btn' data-action='approve-batch' data-batch-id='{b.BatchId}'>Approve All</button>
                <button type='button' class='btn btn-outline-danger btn-sm appr-btn' data-action='reject-batch' data-batch-id='{b.BatchId}'>Reject</button>"
              : "<span class='badge text-bg-warning'>Pending</span>")}
  <button type='button' class='btn btn-sm btn-outline-secondary ms-2 appr-batch-details' data-batch-id='{b.BatchId}' data-kpi-id='{b.KpiId}'>Details</button>
</div>";
                }
                else if (b.ApprovalStatus == "approved")
                {
                    headerRight = $@"<span class='badge text-bg-success'>Approved</span>
<button type='button' class='btn btn-sm btn-outline-secondary ms-2 appr-batch-details' data-batch-id='{b.BatchId}' data-kpi-id='{b.KpiId}'>Details</button>";
                }
                else
                {
                    headerRight = $@"<span class='badge text-bg-danger'>Rejected</span>
<div class='small text-muted mt-1'>Reason: {H(b.RejectReason)}</div>
<button type='button' class='btn btn-sm btn-outline-secondary mt-1 appr-batch-details' data-batch-id='{b.BatchId}' data-kpi-id='{b.KpiId}'>Details</button>";
                }

                var freq = string.IsNullOrWhiteSpace(b.Frequency) ? "—" : b.Frequency;
                var perText = (b.PeriodMin.HasValue && b.PeriodMax.HasValue)
                                ? $"{b.PeriodMin}–{b.PeriodMax}" : "—";

                sb.Append($@"
<div class='appr-card border rounded-3 bg-white p-3 mb-2' data-batch-id='{b.BatchId}' data-kpi-id='{b.KpiId}'>
  <div class='d-flex justify-content-between align-items-start'>
    <div>
      <div class='fw-bold'>{code}</div>
      <div class='small text-muted'>Year: {b.Year}, Periods {H(perText)} • Freq: {H(freq)}</div>
      <div class='small text-muted'>Submitted by <strong>{H(b.SubmittedBy)}</strong> at {F(b.SubmittedAt)}</div>
    </div>
    <div class='text-end'>{headerRight}</div>
  </div>");

                if (rows.Count == 0)
                {
                    sb.Append("<div class='text-muted small mt-2'>No changes.</div>");
                }
                else
                {
                    sb.Append(@"
  <div class='table-responsive mt-3'>
    <table class='table table-sm align-middle mb-0'>
      <thead class='table-light'>
        <tr>
          <th style='width:34%'>Period</th>
          <th style='width:33%'>Actual</th>
          <th style='width:33%'>Forecast</th>
        </tr>
      </thead>
      <tbody>");

                    foreach (var r in rows)
                    {
                        facts.TryGetValue(r.KpiFactId, out var fh);
                        var perLabel = PeriodLabel(fh?.Period);

                        var actCell = DiffNum(fh?.ActualValue, r.ProposedActualValue);
                        var fctCell = DiffNum(fh?.ForecastValue, r.ProposedForecastValue);

                        sb.Append($@"
        <tr>
          <td>
            <div>{H(perLabel)}</div>
            <div class='small text-muted'>Submitted: {F(r.SubmittedAt)}</div>
          </td>
          <td>{actCell}</td>
          <td>{fctCell}</td>
        </tr>");
                    }

                    sb.Append(@"
      </tbody>
    </table>
  </div>");
                }

                if (b.ApprovalStatus != "pending")
                {
                    sb.Append($@"
  <div class='small text-muted mt-2'>
    Reviewed by <strong>{H(b.ReviewedBy)}</strong> at {F(b.ReviewedAt)}
  </div>");
                }

                sb.Append("</div>");
            }

            return Content(sb.ToString(), "text/html");
        }

        // ------------------------
        // Single-row Details JSON
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> ChangeOverlayInfo(decimal changeId, CancellationToken ct = default)
        {
            var ch = await _db.KpiFactChanges
                .AsNoTracking()
                .Where(x => x.KpiFactChangeId == changeId)
                .Select(x => new
                {
                    Change = x,
                    Fact = x.KpiFact,
                    Period = x.KpiFact != null ? x.KpiFact.Period : null,
                    Kpi = x.KpiFact != null ? x.KpiFact.Kpi : null,
                    Pillar = x.KpiFact != null && x.KpiFact.Kpi != null ? x.KpiFact.Kpi.Pillar : null,
                    Objective = x.KpiFact != null && x.KpiFact.Kpi != null ? x.KpiFact.Kpi.Objective : null
                })
                .FirstOrDefaultAsync(ct);

            if (ch == null || ch.Fact == null || ch.Period == null || ch.Kpi == null)
                return NotFound(new { ok = false, error = "Change or KPI/Period not found." });

            var myEmp = await MyEmpIdAsync(ct);
            if (!(_admin.IsAdmin(User) || _admin.IsSuperAdmin(User)))
            {
                var ownsOrEdits = await (
                    from f in _db.KpiFacts
                    join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                    where f.KpiFactId == ch.Fact.KpiFactId
                          && (yp.OwnerEmpId == myEmp || yp.EditorEmpId == myEmp)
                    select 1
                ).AnyAsync(ct);

                if (!ownsOrEdits)
                    return StatusCode(403, new { ok = false, error = "Not allowed." });
            }

            var kpiText = $"{(ch.Pillar?.PillarCode ?? "")}.{(ch.Objective?.ObjectiveCode ?? "")} {(ch.Kpi?.KpiCode ?? "")} — {(ch.Kpi?.KpiName ?? "-")}";
            var per = ch.Period;
            var periodText = PeriodLabel(per);

            return Json(new
            {
                ok = true,
                kpiId = ch.Fact.KpiId,
                kpiText,
                period = new
                {
                    year = per.Year,
                    month = per.MonthNum,
                    quarter = per.QuarterNum,
                    label = periodText
                },
                proposed = new
                {
                    actual = ch.Change.ProposedActualValue,
                    target = ch.Change.ProposedTargetValue,
                    forecast = ch.Change.ProposedForecastValue,
                    status = ch.Change.ProposedStatusCode
                },
                submittedBy = ch.Change.SubmittedBy,
                submittedAt = ch.Change.SubmittedAt
            });
        }

        // ------------------------
        // Batch Details JSON
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> ChangeOverlayInfoBatch(decimal batchId, CancellationToken ct = default)
        {
            var b = await _db.KpiFactChangeBatches.AsNoTracking()
                     .FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
            if (b == null) return NotFound(new { ok = false, error = "Batch not found." });

            if (!(_admin.IsAdmin(User) || _admin.IsSuperAdmin(User)))
            {
                var myEmp = await MyEmpIdAsync(ct);
                if (string.IsNullOrWhiteSpace(myEmp))
                    return StatusCode(403, new { ok = false, error = "Not allowed." });

                var allowed = await _db.KpiYearPlans.AsNoTracking()
                                .AnyAsync(p => p.KpiYearPlanId == b.KpiYearPlanId &&
                                               (p.OwnerEmpId == myEmp || p.EditorEmpId == myEmp), ct);
                if (!allowed) return StatusCode(403, new { ok = false, error = "Not allowed." });
            }

            var rows = await _db.KpiFactChanges.AsNoTracking()
                         .Where(c => c.BatchId == batchId)
                         .Select(c => new
                         {
                             c.KpiFactId,
                             c.ProposedActualValue,
                             c.ProposedForecastValue,
                             c.SubmittedBy,
                             c.SubmittedAt
                         })
                         .ToListAsync(ct);

            var factIds = rows.Select(r => r.KpiFactId).Distinct().ToList();
            var periods = await _db.KpiFacts.AsNoTracking()
                            .Where(f => factIds.Contains(f.KpiFactId))
                            .Select(f => new { f.KpiFactId, P = f.Period })
                            .ToDictionaryAsync(x => x.KpiFactId, x => x.P, ct);

            var k = await _db.DimKpis.AsNoTracking()
                     .Where(x => x.KpiId == b.KpiId)
                     .Select(x => new
                     {
                         x.KpiId,
                         x.KpiCode,
                         x.KpiName,
                         PillarCode = x.Pillar != null ? x.Pillar.PillarCode : null,
                         ObjectiveCode = x.Objective != null ? x.Objective.ObjectiveCode : null
                     })
                     .FirstOrDefaultAsync(ct);

            string kpiText = (k == null)
                ? $"KPI {b.KpiId}"
                : $"{(k.PillarCode ?? "")}.{(k.ObjectiveCode ?? "")} {(k.KpiCode ?? "")} — {(k.KpiName ?? "-")}";

            var items = rows.Select(r =>
            {
                periods.TryGetValue(r.KpiFactId, out var p);
                return new
                {
                    period = new
                    {
                        year = p?.Year,
                        month = p?.MonthNum,
                        quarter = p?.QuarterNum,
                        label = PeriodLabel(p)
                    },
                    proposed = new
                    {
                        actual = r.ProposedActualValue,
                        forecast = r.ProposedForecastValue
                    },
                    submittedBy = r.SubmittedBy,
                    submittedAt = r.SubmittedAt
                };
            })
            .OrderBy(x =>
            {
                var y = x.period.year ?? 0;
                var m = x.period.month ?? (x.period.quarter != null ? x.period.quarter * 3 : 0);
                return y * 100 + (m ?? 0);
            })
            .ToList();

            return Json(new
            {
                ok = true,
                kpiId = b.KpiId,
                kpiText,
                frequency = string.IsNullOrWhiteSpace(b.Frequency) ? null : b.Frequency,
                submittedBy = b.SubmittedBy,
                submittedAt = b.SubmittedAt,
                items
            });
        }
    }
}
