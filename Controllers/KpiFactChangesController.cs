using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net; // WebUtility.HtmlEncode
using System.Collections.Generic;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;                  // IKpiAccessService, IEmployeeDirectory
using KPIMonitor.Services.Abstractions;     // IKpiFactChangeService, IKpiFactChangeBatchService, IEmailSender
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
        private readonly IEmailSender _email;            // DO NOT TOUCH email behavior
        private readonly IEmployeeDirectory _dir;

        // keep http so images render in intranet mail clients (not used by approvals page)
        private const string InboxUrl = "http://kpimonitor.badea.local/kpimonitor/KpiFactChanges/Inbox";
        private const string LogoUrl = "http://kpimonitor.badea.local/kpimonitor/images/logo-en.png";

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
            return rec?.EmpId; // BADEA_ADDONS.EMPLOYEES.EMP_ID  — your original source of truth
        }

        private static string PeriodLabel(DimPeriod? p)
        {
            if (p == null) return "—";
            if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
            if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
            return p.Year.ToString();
        }

        private static string HtmlEmail(string title, string bodyHtml)
        {
            string esc(string s) => WebUtility.HtmlEncode(s);
            return $@"
<!DOCTYPE html>
<html lang='en'>
<head>
  <meta charset='UTF-8'/>
  <meta name='viewport' content='width=device-width, initial-scale=1.0'/>
  <title>{esc(title)}</title>
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

        // ------------------------
        // Small JSONs
        // ------------------------
        [HttpGet]
        public async Task<IActionResult> HasPending(decimal kpiFactId)
        {
            var pending = await _svc.HasPendingAsync(kpiFactId);
            return Json(new { pending });
        }

        // EmpId-based count (as originally)
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
                    submittedBy,
                    notifyOwner: true,        // SINGLE submit → send owner email here
                    batchId: null
                );

                // Auto-approve if SuperAdmin (unchanged)
                if (_admin.IsSuperAdmin(User))
                {
                    await _svc.ApproveAsync(change.KpiFactChangeId, submittedBy);
                    change.ApprovalStatus = "approved"; // keep response consistent
                }

                var msg = string.Equals(change.ApprovalStatus, "approved", StringComparison.OrdinalIgnoreCase)
                          ? "Saved & auto-approved."
                          : "Submitted for approval.";

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
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message });
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
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message });
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
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message });
            }
        }

        // ------------------------
        // Inbox page + HTML fragments (unchanged)
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
                        // own submissions OR KPIs where you're configured as Editor
                        (c.SubmittedBy != null && c.SubmittedBy.ToUpper() == mySamUp) ||
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
        // Submit a batch (ONE email total)
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
            Dictionary<int, decimal?>? TargetQuarters,
            CancellationToken ct = default)
        {
            try
            {
                var traceId = HttpContext.TraceIdentifier ?? Guid.NewGuid().ToString("n");

                var plan = await _db.KpiYearPlans
                    .Include(p => p.Period)
                    .AsNoTracking()
                    .Where(p => p.KpiId == kpiId && p.IsActive == 1 && p.Period != null)
                    .OrderByDescending(p => p.KpiYearPlanId)
                    .FirstOrDefaultAsync(ct);

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
                    .ToListAsync(ct);

                bool monthly = facts.Any(f => f.Period!.MonthNum.HasValue) || (!facts.Any() && isMonthly);

                var postedActuals = monthly ? (Actuals ?? new Dictionary<int, decimal?>())
                                             : (ActualQuarters ?? new Dictionary<int, decimal?>());
                var postedForecast = monthly ? (Forecasts ?? new Dictionary<int, decimal?>())
                                             : (ForecastQuarters ?? new Dictionary<int, decimal?>());
                var postedTargets = monthly ? (Targets ?? new Dictionary<int, decimal?>())
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

                // Create the batch first so child submits are linked
                var batchId = await _batches.CreateBatchAsync(
                    kpiId,
                    plan.KpiYearPlanId,
                    year,
                    monthly,
                    null,
                    null,
                    submittedBy,
                    0,
                    0,
                    ct
                );

                foreach (var f in facts)
                {
                    int key = monthly ? (f.Period!.MonthNum ?? 0) : (f.Period!.QuarterNum ?? 0);
                    if (key == 0) { skipped++; continue; }

                    var aProvided = postedActuals.TryGetValue(key, out var newA) && newA.HasValue;
                    var fProvided = postedForecast.TryGetValue(key, out var newF) && newF.HasValue;
                    var tProvided = postedTargets.TryGetValue(key, out var newT) && newT.HasValue;

                    var changeA = aProvided && (f.ActualValue != newA);
                    var changeF = fProvided && (f.ForecastValue != newF);
                    var changeT = isSuperAdmin && tProvided && (f.TargetValue != newT);

                    if (!changeA && !changeF && !changeT) { skipped++; continue; }
                    if ((changeA && !editableA.Contains(key)) || (changeF && !editableF.Contains(key))) { skipped++; continue; }
                    if (await _svc.HasPendingAsync(f.KpiFactId)) { skipped++; continue; }

                    try
                    {
                        var ch = await _svc.SubmitAsync(
                            f.KpiFactId,
                            changeA ? newA : null,   // Actual
                            changeT ? newT : null,   // Target (super-admin only)
                            changeF ? newF : null,   // Forecast
                            null,                    // Status
                            submittedBy,
                            notifyOwner: false,      // BATCH children → NO owner email here
                            batchId: batchId
                        );

                        if (isSuperAdmin)
                        {
                            await _svc.ApproveAsync(ch.KpiFactChangeId, submittedBy, suppressEmail: true);
                        }

                        created++;
                        createdIds.Add(ch.KpiFactChangeId);
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

                // finalize batch counts + range
                var b = await _db.KpiFactChangeBatches.FirstOrDefaultAsync(x => x.BatchId == batchId, ct);
                if (b != null)
                {
                    b.RowCount = created;
                    b.SkippedCount = skipped;
                    b.PeriodMin = (minKey == int.MaxValue) ? (int?)null : minKey;
                    b.PeriodMax = (maxKey == int.MinValue) ? (int?)null : maxKey;
                    await _db.SaveChangesAsync(ct);
                }

                if (isSuperAdmin)
                {
                    _log.LogInformation("SUPERADMIN_BYPASS SubmitBatch user={User} kpiId={KpiId} planId={PlanId} year={Year} monthly={Monthly} created={Created} skipped={Skipped} batchId={BatchId} traceId={TraceId}",
                        Sam(), kpiId, plan.KpiYearPlanId, year, monthly, created, skipped, batchId, traceId);
                }

                // === ONE batch summary email to Owner (same as your working flow) ===
                var kpiHead = await _db.DimKpis.AsNoTracking()
                    .Where(x => x.KpiId == kpiId)
                    .Select(x => new
                    {
                        x.KpiCode,
                        x.KpiName,
                        PillarCode = x.Pillar != null ? x.Pillar.PillarCode : null,
                        ObjectiveCode = x.Objective != null ? x.Objective.ObjectiveCode : null
                    })
                    .FirstOrDefaultAsync(ct);

                var kpiText = (kpiHead == null)
                    ? $"KPI {kpiId}"
                    : $"{(kpiHead.PillarCode ?? "")}.{(kpiHead.ObjectiveCode ?? "")} {(kpiHead.KpiCode ?? "")} — {(kpiHead.KpiName ?? "-")}";

                string? ownerEmail = null;
                var ownerEmpId = plan.OwnerEmpId;
                if (!string.IsNullOrWhiteSpace(ownerEmpId))
                {
                    var ownerLogin = await _dir.TryGetLoginByEmpIdAsync(ownerEmpId, ct);
                    if (!string.IsNullOrWhiteSpace(ownerLogin))
                    {
                        var samLogin = ownerLogin.Trim();
                        if (samLogin.Contains("@"))
                        {
                            ownerEmail = samLogin;
                        }
                        else
                        {
                            var bs = samLogin.LastIndexOf('\\');
                            if (bs >= 0 && bs < samLogin.Length - 1) samLogin = samLogin[(bs + 1)..];
                            var at = samLogin.IndexOf('@');
                            if (at > 0) samLogin = samLogin[..at];
                            ownerEmail = $"{samLogin}@badea.org";
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(ownerEmail))
                {
                    var subject = "KPI batch submitted for approval";
                    var perText = (b?.PeriodMin.HasValue == true && b?.PeriodMax.HasValue == true)
                                    ? $"{b!.PeriodMin}–{b!.PeriodMax}" : "—";

                    var bodyHtml = $@"
<p>A batch of <strong>{created}</strong> change(s) was submitted for <em>{WebUtility.HtmlEncode(kpiText)}</em>.</p>
<p>Year: <strong>{year}</strong> • Frequency: <strong>{(monthly ? "Monthly" : "Quarterly")}</strong> • Periods: <strong>{WebUtility.HtmlEncode(perText)}</strong></p>
<p>Submitted by <strong>{WebUtility.HtmlEncode(submittedBy)}</strong>.</p>";

                    await _email.SendEmailAsync(ownerEmail, subject, HtmlEmail(subject, bodyHtml));
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
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message, traceId = HttpContext.TraceIdentifier });
            }
        }

        // ------------------------
        // Batch owner/admin check — unchanged
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
        // Approve/Reject entire batch — unchanged
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
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message });
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
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return BadRequest(new { ok = false, error = ex.GetBaseException()?.Message ?? ex.Message });
            }
        }

        // ------------------------
        // List batches (cards) — unchanged EmpId logic (plus own submissions)
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ListBatchesHtml(string? status = "pending", string? modeOverride = null, CancellationToken ct = default)
        {
            var s = (status ?? "pending").Trim().ToLowerInvariant();
            if (s != "pending" && s != "approved" && s != "rejected") s = "pending";

            var isAdmin = _admin.IsAdmin(User) || _admin.IsSuperAdmin(User);
            var myEmp = await MyEmpIdAsync(ct);
            var mySam = Sam();
            var mySamUp = (mySam ?? "").ToUpperInvariant();

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
                    // own batches OR KPIs where you're configured as Editor
                    (b.SubmittedBy != null && b.SubmittedBy.ToUpper() == mySamUp)
                    ||
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
  {(canAct ? $@"<button type='button' id='btn-approve-all' class='btn btn-success btn-sm appr-btn' data-action='approve-batch' data-batch-id='{b.BatchId}'>Approve</button>
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
        // Single-row Details JSON (unchanged)
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
        // Batch Details JSON (unchanged)
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
        // ------------------------
        // Editors stats (admin only)
        // ------------------------
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditorStatsHtml(CancellationToken ct = default)
        {
            // Hard guard: only Admin / SuperAdmin
            if (!(_admin.IsAdmin(User) || _admin.IsSuperAdmin(User)))
            {
                return StatusCode(403, "Not allowed.");
            }

            // 1) Collect distinct editor EmpIds from active plans
            var editorEmpIds = await _db.KpiYearPlans
                .AsNoTracking()
                .Where(p => p.EditorEmpId != null && p.IsActive != 0)
                .Select(p => p.EditorEmpId!)
                .Distinct()
                .ToListAsync(ct);

            if (editorEmpIds.Count == 0)
            {
                return Content("<div class='text-muted small'>No editors configured.</div>", "text/html; charset=utf-8");
            }

            // One row per (Editor, Indicator)
            var rows = new List<(
                string EmpId,
                string Name,
                string? Login,
                string IndicatorLabel,
                string? OwnerName,
                DateTime? LastSubmittedAt,
                string? ApprovalStatus
            )>();

            foreach (var empId in editorEmpIds)
            {
                if (string.IsNullOrWhiteSpace(empId))
                    continue;

                // Editor info (name + login)
                var rec = await _dir.TryGetByEmpIdAsync(empId, ct);
                var login = await _dir.TryGetLoginByEmpIdAsync(empId, ct);
                var sam = Sam(login); // normalize DOMAIN\user / user@mail → bare SAM

                if (string.IsNullOrWhiteSpace(sam))
                    continue;

                var samUp = sam.ToUpperInvariant();

                // 2) All active indicators (plans) for this editor
                var plans = await _db.KpiYearPlans
                    .AsNoTracking()
                    .Include(p => p.Kpi)
                        .ThenInclude(k => k.Pillar)
                    .Include(p => p.Kpi)
                        .ThenInclude(k => k.Objective)
                    .Where(p => p.EditorEmpId == empId && p.IsActive != 0)
                    .ToListAsync(ct);

                foreach (var plan in plans)
                {
                    // Build indicator label: e.g. "1.1 v — KPI Name"
                    string indicatorLabel;
                    var kpi = plan.Kpi;

                    if (kpi != null)
                    {
                        var pillCode = kpi.Pillar?.PillarCode ?? "";
                        var objCode = kpi.Objective?.ObjectiveCode ?? "";
                        var codePart = $"{pillCode}.{objCode} {kpi.KpiCode}".Trim();
                        var namePart = kpi.KpiName ?? "";

                        if (!string.IsNullOrWhiteSpace(codePart) && !string.IsNullOrWhiteSpace(namePart))
                            indicatorLabel = $"{codePart} — {namePart}";
                        else if (!string.IsNullOrWhiteSpace(namePart))
                            indicatorLabel = namePart;
                        else
                            indicatorLabel = codePart;
                    }
                    else
                    {
                        indicatorLabel = "(no KPI)";
                    }

                    // Owner name (from OwnerEmpId where possible)
                    string? ownerName = null;
                    if (!string.IsNullOrWhiteSpace(plan.OwnerEmpId))
                    {
                        var ownerRec = await _dir.TryGetByEmpIdAsync(plan.OwnerEmpId, ct);
                        ownerName = ownerRec?.NameEng ?? plan.OwnerEmpId;
                    }
                    else if (!string.IsNullOrWhiteSpace(plan.Owner))
                    {
                        ownerName = plan.Owner;
                    }

                    // 3) Latest submission for this indicator by this editor (by date)
                    var latestChange = await _db.KpiFactChanges
                        .AsNoTracking()
                        .Include(c => c.KpiFact)
                        .Where(c =>
                            c.KpiFact.KpiYearPlanId == plan.KpiYearPlanId &&
                            c.SubmittedBy != null &&
                            c.SubmittedBy.ToUpper() == samUp)
                        .OrderByDescending(c => c.SubmittedAt)
                        .FirstOrDefaultAsync(ct);

                    DateTime? lastSubmittedAt = latestChange?.SubmittedAt;
                    string? approvalStatus = latestChange?.ApprovalStatus;

                    rows.Add((
                        EmpId: empId,
                        Name: rec?.NameEng ?? empId,
                        Login: login,
                        IndicatorLabel: indicatorLabel,
                        OwnerName: ownerName,
                        LastSubmittedAt: lastSubmittedAt,
                        ApprovalStatus: approvalStatus
                    ));
                }
            }

            if (rows.Count == 0)
            {
                return Content("<div class='text-muted small'>No editor indicators found.</div>", "text/html; charset=utf-8");
            }

            // 4) Sort & group by editor so we only show the editor once
            var grouped = rows
                .OrderBy(r => r.Name)
                .ThenBy(r => r.IndicatorLabel)
                .GroupBy(r => new { r.EmpId, r.Name, r.Login });

            static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
            static string F(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "—"; // DATE ONLY
            static string StatusLabel(string? s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "Pending";
                s = s.Trim().ToLowerInvariant();
                return s switch
                {
                    "approved" => "Approved",
                    "rejected" => "Rejected",
                    "pending" => "Pending",
                    _ => s
                };
            }

            // 5) Render table: editor once, then all their indicators
            var sb = new StringBuilder();
            sb.AppendLine("<div class='table-responsive'>");
            sb.AppendLine("<table class='table table-sm table-hover align-middle mb-0'>");
            sb.AppendLine("<thead><tr>");
            sb.AppendLine("<th>Indicator</th>");
            sb.AppendLine("<th>Owner</th>");
            sb.AppendLine("<th>Approval Status</th>");
            sb.AppendLine("<th>Last Submission</th>");
            sb.AppendLine("</tr></thead><tbody>");

            foreach (var group in grouped)
            {
                var displayName = group.Key.Name;
                var login = group.Key.Login;

                // Editor header row (editor shown once)
                sb.Append("<tr class='table-light'>");
                sb.Append("<td colspan='4'><strong>")
                  .Append(H(displayName));

                if (!string.IsNullOrWhiteSpace(login))
                {
                    sb.Append("</strong> <span class='text-muted small'>(")
                      .Append(H(login))
                      .Append(")</span>");
                }
                else
                {
                    sb.Append("</strong>");
                }

                sb.Append("</td></tr>");

                // Indicator rows for this editor
                foreach (var r in group)
                {
                    // Has this indicator ever had a submission?
                    var hasSubmission = r.LastSubmittedAt.HasValue;

                    // If there was a submission:
                    //   - null/empty status => "Pending"
                    // If there was NO submission:
                    //   - show "—"
                    var displayStatus = hasSubmission
                        ? StatusLabel(r.ApprovalStatus)
                        : "—";

                    sb.Append("<tr>");
                    sb.Append("<td>").Append(H(r.IndicatorLabel)).Append("</td>");
                    sb.Append("<td>").Append(H(r.OwnerName ?? "—")).Append("</td>");
                    sb.Append("<td>").Append(H(displayStatus)).Append("</td>");
                    sb.Append("<td>").Append(H(F(r.LastSubmittedAt))).Append("</td>");
                    sb.AppendLine("</tr>");
                }

            }

            sb.AppendLine("</tbody></table></div>");

            return Content(sb.ToString(), "text/html; charset=utf-8");
        }

    }
}
