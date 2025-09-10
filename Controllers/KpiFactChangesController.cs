using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net; // WebUtility.HtmlEncode
using System.Security.Claims;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;                  // IKpiAccessService, IEmployeeDirectory
using KPIMonitor.Services.Abstractions;     // IKpiFactChangeService
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KPIMonitor.Controllers
{
    public class KpiFactChangesController : Controller
    {
        private readonly IKpiFactChangeService _svc;
        private readonly IKpiAccessService _acl;
        private readonly AppDbContext _db;
        private readonly global::IAdminAuthorizer _admin;

        public KpiFactChangesController(
            IKpiFactChangeService svc,
            IKpiAccessService acl,
            AppDbContext db,
            global::IAdminAuthorizer admin)
        {
            _svc = svc;
            _acl = acl;
            _db = db;
            _admin = admin;
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
            var dir = HttpContext.RequestServices.GetRequiredService<IEmployeeDirectory>();
            var rec = await dir.TryGetByUserIdAsync(sam, ct);
            return rec?.EmpId; // BADEA_ADDONS.EMPLOYEES.EMP_ID
        }

        private static string PeriodLabel(DimPeriod? p)
        {
            if (p == null) return "—";
            if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
            if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
            return p.Year.ToString();
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

        // Owners: pending approvals count (admin = all)
        [HttpGet]
        public async Task<IActionResult> PendingCount()
        {
            if (_admin.IsAdmin(User))
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
        // Submit / Approve / Reject
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
                // store domainless username (e.g., "walid.salem")
                var submittedBy = Sam();
                if (string.IsNullOrWhiteSpace(submittedBy)) submittedBy = "editor";

                var change = await _svc.SubmitAsync(
                    kpiFactId,
                    ProposedActualValue, ProposedTargetValue, ProposedForecastValue,
                    ProposedStatusCode,
                    submittedBy);

                return Json(new { ok = true, changeId = change.KpiFactChangeId, message = "Submitted for approval." });
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

                var reviewer = Sam();
                if (string.IsNullOrWhiteSpace(reviewer)) reviewer = "owner";

                await _svc.RejectAsync(changeId, reviewer, reason);
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.Message });
            }
        }

        // ------------------------
        // Inbox page + HTML fragment
        // ------------------------
        [HttpGet]
        public IActionResult Inbox() => View();

        /// <summary>
        /// HTML fragment: auto-mode.
        /// Owners/Admin => approvals; others => requests (by editor)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListHtml(string? status = "pending", string? modeOverride = null, CancellationToken ct = default)
        {
            var s = (status ?? "pending").Trim().ToLowerInvariant();
            if (s != "pending" && s != "approved" && s != "rejected") s = "pending";

            var isAdmin = _admin.IsAdmin(User);
            var myEmp = await MyEmpIdAsync(ct);
            var mySam = Sam();
            var mySamUp = mySam.ToUpperInvariant();

            // Decide MODE automatically (no client choice):
            // Admin => owner mode (see all)
            // If user owns any plan => owner mode
            // else => editor mode (my requests on plans where I'm editor)
            var isOwnerSomewhere = !string.IsNullOrWhiteSpace(myEmp) &&
                                   await _db.KpiYearPlans.AsNoTracking()
                                       .AnyAsync(p => p.OwnerEmpId == myEmp, ct);

            var mode = (isAdmin || isOwnerSomewhere) ? "owner" : "editor";
            // Optional query override: ?mode=editor or ?mode=owner
            var forced = (modeOverride ?? Request?.Query["mode"].FirstOrDefault())?.Trim().ToLowerInvariant();
            if (forced == "editor")
            {
                mode = "editor";
            }
            else if (forced == "owner" && (isAdmin || isOwnerSomewhere))
            {
                mode = "owner";
            }

            // Base query by status
            var q = _db.KpiFactChanges.AsNoTracking()
                                      .Where(c => c.ApprovalStatus == s);

            if (mode == "owner" && !isAdmin)
            {
                // Only changes for plans I OWN
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
                    // Fallback: show items I submitted if EmpId mapping is missing
                    q = q.Where(c => c.SubmittedBy != null && c.SubmittedBy.ToUpper() == mySamUp);
                }
                else
                {
                    // Only requests on plans where I am the EDITOR
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
            // else admin: no extra filter (see all)

            // Pull rows
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
                    c.RejectReason
                })
                .ToListAsync(ct);

            // For diffs + header (KPI/period codes), fetch minimal head info
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

            // helpers for HTML
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

                    // Build KPI header strings
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
            if (_admin.IsAdmin(User)) return true;

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
        // NEW (additive only): ChangeOverlayInfo for Details modal/chart
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> ChangeOverlayInfo(decimal changeId, CancellationToken ct = default)
        {
            // Load the change + fact + period + kpi (with codes)
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

            // Access: admin OR owner of plan OR editor of plan
            var myEmp = await MyEmpIdAsync(ct);
            if (!_admin.IsAdmin(User))
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

            // Build texts (same style you use in ListHtml)
            var kpiText = $"{(ch.Pillar?.PillarCode ?? "")}.{(ch.Objective?.ObjectiveCode ?? "")} {(ch.Kpi?.KpiCode ?? "")} — {(ch.Kpi?.KpiName ?? "-")}";
            var per = ch.Period;
            var periodText = PeriodLabel(per);

            // Return overlay info; front-end will fetch base chart from RedBoard/GetKpiPresentation?kpiId=...
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
    }
}
