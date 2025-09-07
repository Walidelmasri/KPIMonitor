using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net; // for WebUtility.HtmlEncode
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using KPIMonitor.Services.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Controllers
{
    public class KpiFactChangesController : Controller
    {
        private readonly IKpiFactChangeService _svc;
        private readonly IKpiAccessService _acl;
        private readonly AppDbContext _db;

        public KpiFactChangesController(
            IKpiFactChangeService svc,
            IKpiAccessService acl,
            AppDbContext db)
        {
            _svc = svc;
            _acl = acl;
            _db = db;
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
            var user = (User?.Identity?.Name ?? string.Empty).ToLowerInvariant();

            if (User.IsInRole("Admin"))
            {
                var countAll = await _db.KpiFactChanges
                    .AsNoTracking()
                    .CountAsync(c => c.ApprovalStatus == "pending");
                return Json(new { count = countAll });
            }

            var count = await (
                from c in _db.KpiFactChanges.AsNoTracking()
                join f in _db.KpiFacts.AsNoTracking() on c.KpiFactId equals f.KpiFactId
                join yp in _db.KpiYearPlans.AsNoTracking() on f.KpiYearPlanId equals yp.KpiYearPlanId
                where c.ApprovalStatus == "pending"
                      && yp.Owner != null
                      && yp.Owner.ToLower() == user
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
                var user = User?.Identity?.Name ?? "editor";
                var change = await _svc.SubmitAsync(
                    kpiFactId,
                    ProposedActualValue, ProposedTargetValue, ProposedForecastValue,
                    ProposedStatusCode,
                    user);

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
                // ACL: load change → plan → check
                var ch = await _db.KpiFactChanges
                    .Include(x => x.KpiFact)
                    .FirstOrDefaultAsync(x => x.KpiFactChangeId == changeId);

                if (ch == null) return BadRequest(new { ok = false, error = "Change request not found." });
                if (ch.KpiFact == null) return BadRequest(new { ok = false, error = "KPI fact missing." });
                if (!await IsOwnerOrAdminForChangeAsync(changeId))
                    return StatusCode(403, new { ok = false, error = "Only the KPI Owner (or Admin) can approve this change." });


                var reviewer = User?.Identity?.Name ?? "owner";
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


                var reviewer = User?.Identity?.Name ?? "owner";
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
        /// HTML fragment for the approvals list. Default = pending.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListHtml(string? status = "pending")
        {
            status = (status ?? "pending").Trim().ToLowerInvariant();
            if (status != "pending" && status != "approved" && status != "rejected")
                status = "pending";

            var isAdmin = User.IsInRole("Admin");
            var user = (User?.Identity?.Name ?? string.Empty).ToLowerInvariant();

            // base filter by status
            var q = _db.KpiFactChanges
                .AsNoTracking()
                .Include(c => c.KpiFact)!.ThenInclude(f => f.Period)
                .Include(c => c.KpiFact)!.ThenInclude(f => f.Kpi)!.ThenInclude(k => k.Objective)!.ThenInclude(o => o.Pillar)
                .Where(c => status == "pending" ? c.ApprovalStatus == "pending"
                         : status == "approved" ? c.ApprovalStatus == "approved"
                                                : c.ApprovalStatus == "rejected");

            // owner-only unless admin
            if (!isAdmin)
            {
                q = q.Where(c =>
                    _db.KpiYearPlans.Any(yp =>
                        c.KpiFact != null &&
                        yp.KpiYearPlanId == c.KpiFact.KpiYearPlanId &&
                        yp.Owner != null &&
                        yp.Owner.ToLower() == user));
            }

            var items = await q.OrderByDescending(c => c.SubmittedAt).ToListAsync();

            // (keep the rest of your method: H(), F(), LabelFor(), DiffCell(), StatusCell(), sb builder, return Content(...))


            static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
            static string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";
            static string LabelFor(DimPeriod? p)
            {
                if (p == null) return "—";
                if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
                if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
                return p.Year.ToString();
            }

            string DiffCell(string title, decimal? curV, decimal? newV)
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

            string StatusCell(string cur, string prop)
            {
                cur = cur?.Trim() ?? "";
                prop = prop?.Trim() ?? "";
                var changed = (!string.IsNullOrWhiteSpace(prop) && !string.Equals(cur, prop, StringComparison.OrdinalIgnoreCase));
                var cls = changed ? "appr-diff" : "text-muted";
                string Label(string code) => code.ToLowerInvariant() switch
                {
                    "conforme" => "Ok",
                    "ecart" => "Needs Attention",
                    "rattrapage" => "Catching Up",
                    "attente" => "Data Missing",
                    _ => string.IsNullOrWhiteSpace(code) ? "—" : code
                };
                return $@"
<div class='appr-cell'>
  <div class='small text-muted'>Status</div>
  <div class='{cls}'>{H(Label(cur))} → <strong>{H(Label(prop))}</strong></div>
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
                    var f = c.KpiFact!;
                    var k = f.Kpi!;
                    var o = k.Objective!;
                    var p = o.Pillar!;
                    var code = $"{H(p.PillarCode ?? "")}.{H(o.ObjectiveCode ?? "")} {H(k.KpiCode ?? "")}";
                    var name = H(k.KpiName ?? "-");
                    var per = LabelFor(f.Period);

                    var rowHead = $@"
<div class='d-flex justify-content-between align-items-start'>
  <div>
    <div class='fw-bold'>{code} — {name}</div>
    <div class='small text-muted'>Period: {H(per)}</div>
    <div class='small text-muted'>Submitted by <strong>{H(c.SubmittedBy)}</strong> at {F(c.SubmittedAt)}</div>
  </div>
  <div class='text-end'>
    {(c.ApprovalStatus == "pending" ? $@"
      <div class='btn-group'>
        <button type='button' class='btn btn-success btn-sm appr-btn' data-action='approve' data-id='{c.KpiFactChangeId}'>Approve</button>
        <button type='button' class='btn btn-outline-danger btn-sm appr-btn' data-action='reject' data-id='{c.KpiFactChangeId}'>Reject</button>
      </div>" :
      c.ApprovalStatus == "approved" ? "<span class='badge text-bg-success'>Approved</span>" :
                                        $"<span class='badge text-bg-danger'>Rejected</span><div class='small text-muted mt-1'>Reason: {H(c.RejectReason)}</div>"
    )}
  </div>
</div>";

                    var rowDiffs = $@"
<div class='row g-3 mt-2'>
  <div class='col-6 col-md-3'>{DiffCell("Actual", f.ActualValue, c.ProposedActualValue)}</div>
  <div class='col-6 col-md-3'>{DiffCell("Target", f.TargetValue, c.ProposedTargetValue)}</div>
  <div class='col-6 col-md-3'>{DiffCell("Forecast", f.ForecastValue, c.ProposedForecastValue)}</div>
  <div class='col-6 col-md-3'>{StatusCell(f.StatusCode ?? "", c.ProposedStatusCode ?? "")}</div>
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
            if (User.IsInRole("Admin")) return true;

            var user = (User?.Identity?.Name ?? string.Empty).ToLowerInvariant();

            var owner = await (
                from c in _db.KpiFactChanges
                join f in _db.KpiFacts on c.KpiFactId equals f.KpiFactId
                join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
                where c.KpiFactChangeId == changeId
                select yp.Owner
            ).FirstOrDefaultAsync();

            return !string.IsNullOrWhiteSpace(owner) && owner.ToLower() == user;
        }

    }
}