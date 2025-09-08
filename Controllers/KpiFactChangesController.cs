using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Security.Claims;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using KPIMonitor.Services.Abstractions;
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
// add at top of the file if not present:

// in KpiFactChangesController class, add this helper:
private async Task<string?> CurrentEmpIdAsync(CancellationToken ct = default)
{
    var dir = HttpContext.RequestServices.GetRequiredService<IEmployeeDirectory>();
    var sam = Sam(User?.Identity?.Name);
    if (string.IsNullOrWhiteSpace(sam)) return null;

    var rec = await dir.TryGetByUserIdAsync(sam!, ct);
    return rec?.EmpId; // string
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
    if (_admin.IsAdmin(User))
    {
        var countAll = await _db.KpiFactChanges
            .AsNoTracking()
            .CountAsync(c => c.ApprovalStatus == "pending");
        return Json(new { count = countAll });
    }

    var myEmpId = await CurrentEmpIdAsync();
    if (string.IsNullOrWhiteSpace(myEmpId))
        return Json(new { count = 0 });

    var count = await (
        from c  in _db.KpiFactChanges.AsNoTracking()
        join f  in _db.KpiFacts.AsNoTracking()      on c.KpiFactId     equals f.KpiFactId
        join yp in _db.KpiYearPlans.AsNoTracking()  on f.KpiYearPlanId equals yp.KpiYearPlanId
        where c.ApprovalStatus == "pending"
              && yp.OwnerEmpId != null
              && yp.OwnerEmpId == myEmpId
        select 1
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
                // store the simple username (walid.salem), not DOMAIN\user
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
                // load change → plan for ACL
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

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> ListHtml(string? status = "pending")
{
    var s = (status ?? "pending").Trim().ToLowerInvariant();
    if (s != "pending" && s != "approved" && s != "rejected") s = "pending";

    var isAdmin = _admin.IsAdmin(User);

    var showAll =
        string.Equals(Request.Query["showAll"], "1", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Request.Form["showAll"],  "1", StringComparison.OrdinalIgnoreCase);

    var q = _db.KpiFactChanges
        .AsNoTracking()
        .Where(c => c.ApprovalStatus == s);

    if (!isAdmin && !showAll)
    {
        var myEmpId = await CurrentEmpIdAsync();
        if (string.IsNullOrWhiteSpace(myEmpId))
        {
            // no identity → no items
            return Content("<div class='text-muted small'>No items.</div>", "text/html");
        }

        q = q.Where(c =>
            (from f in _db.KpiFacts
             join yp in _db.KpiYearPlans on f.KpiYearPlanId equals yp.KpiYearPlanId
             where f.KpiFactId == c.KpiFactId
                   && yp.OwnerEmpId != null
                   && yp.OwnerEmpId == myEmpId
             select 1).Any()
        );
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
            c.RejectReason
        })
        .ToListAsync();

    var factIds = items.Select(i => i.KpiFactId).Distinct().ToList();
    var curFacts = await _db.KpiFacts
        .AsNoTracking()
        .Where(f => factIds.Contains(f.KpiFactId))
        .Select(f => new { f.KpiFactId, f.ActualValue, f.TargetValue, f.ForecastValue, f.StatusCode })
        .ToDictionaryAsync(x => x.KpiFactId, x => x);

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
            curFacts.TryGetValue(c.KpiFactId, out var cf);

            var rowHead = $@"
<div class='d-flex justify-content-between align-items-start'>
  <div>
    <div class='fw-bold'>KPI Fact #{H(c.KpiFactId.ToString())}</div>
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
  <div class='col-6 col-md-3'>{DiffNum("Actual",   cf?.ActualValue,   c.ProposedActualValue)}</div>
  <div class='col-6 col-md-3'>{DiffNum("Target",   cf?.TargetValue,   c.ProposedTargetValue)}</div>
  <div class='col-6 col-md-3'>{DiffNum("Forecast", cf?.ForecastValue, c.ProposedForecastValue)}</div>
  <div class='col-6 col-md-3'>{DiffStatus(cf?.StatusCode ?? "",       c.ProposedStatusCode ?? "")}</div>
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

    var myEmpId = await CurrentEmpIdAsync();
    if (string.IsNullOrWhiteSpace(myEmpId)) return false;

    var ownerEmpId = await (
        from c  in _db.KpiFactChanges
        join f  in _db.KpiFacts      on c.KpiFactId     equals f.KpiFactId
        join yp in _db.KpiYearPlans  on f.KpiYearPlanId equals yp.KpiYearPlanId
        where c.KpiFactChangeId == changeId
        select yp.OwnerEmpId
    ).FirstOrDefaultAsync();

    return !string.IsNullOrWhiteSpace(ownerEmpId) && ownerEmpId == myEmpId;
}



[HttpGet]
public async Task<IActionResult> DebugOwner()
{
    var meRaw = User?.Identity?.Name ?? "";
    var meSam = Sam(meRaw);
    var meUp  = meSam.ToUpperInvariant();

    var totalPending = await _db.KpiFactChanges.AsNoTracking()
        .CountAsync(c => c.ApprovalStatus == "pending");

    var minePending = await (
        from c  in _db.KpiFactChanges.AsNoTracking()
        join f  in _db.KpiFacts.AsNoTracking()      on c.KpiFactId      equals f.KpiFactId
        join yp in _db.KpiYearPlans.AsNoTracking()  on f.KpiYearPlanId  equals yp.KpiYearPlanId
        where c.ApprovalStatus == "pending"
              && yp.OwnerLogin != null
              && yp.OwnerLogin.Trim().ToUpper() == meUp
        select c.KpiFactChangeId
    ).CountAsync();

    var sample = await (
        from c  in _db.KpiFactChanges.AsNoTracking()
        join f  in _db.KpiFacts.AsNoTracking()      on c.KpiFactId      equals f.KpiFactId
        join yp in _db.KpiYearPlans.AsNoTracking()  on f.KpiYearPlanId  equals yp.KpiYearPlanId
        where c.ApprovalStatus == "pending"
        orderby c.SubmittedAt descending
        select new {
            c.KpiFactChangeId, c.KpiFactId,
            OwnerLoginRaw = yp.OwnerLogin,
            OwnerLoginUp  = yp.OwnerLogin == null ? null : yp.OwnerLogin.Trim().ToUpper(),
            MeUp          = meUp
        }
    ).Take(10).ToListAsync();

    return Json(new { meRaw, meSam, meUp, totalPending, minePending, sample });
}


    }
}