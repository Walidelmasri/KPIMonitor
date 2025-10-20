using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using System.Text;
using System.Net;
using System.Linq;


namespace KPIMonitor.Controllers
{
    public class RedBoardController : Controller
    {
        private readonly AppDbContext _db;
        public RedBoardController(AppDbContext db) { _db = db; }

        // Page
        [HttpGet]
        public IActionResult Index() => View();

        // List of KPI ids that are "red", ordered by priority asc then name
        [HttpGet]
        public async Task<IActionResult> GetRedKpiIds()
        {
            var redCodes = new[] { "red", "ecart" }; // normalize to lowercase

            // Step 1: latest plan id per KPI (pure grouping = easy to translate)
            var latestIds =
                from p in _db.KpiYearPlans
                where p.IsActive == 1 && p.Period != null
                group p by p.KpiId into g
                select new
                {
                    KpiId = g.Key,
                    MaxPlanId = g.Max(x => x.KpiYearPlanId)
                };

            // Step 2: join back to the actual plan row to get the year & priority
            var latestPlans =
                from lid in latestIds
                join p in _db.KpiYearPlans on
                    new { lid.KpiId, PlanId = lid.MaxPlanId }
                    equals new { p.KpiId, PlanId = p.KpiYearPlanId }
                select new
                {
                    p.KpiId,
                    p.KpiYearPlanId,
                    Year = p.Period!.Year,   // navigation gives us the year
                    p.Priority
                };

            // Step 3a: for each KPI/plan-year, find the *latest* fact by period start date within that same year (with a non-null status)
            var latestFactPerKpi =
                from lp in latestPlans
                join f in _db.KpiFacts on new { lp.KpiId, PlanId = lp.KpiYearPlanId } equals new { f.KpiId, PlanId = f.KpiYearPlanId }
                join per in _db.DimPeriods on f.PeriodId equals per.PeriodId
                where f.IsActive == 1 && per.Year == lp.Year && f.StatusCode != null
                group new { f, per, lp } by new { lp.KpiId, lp.KpiYearPlanId, lp.Year, lp.Priority } into g
                select new
                {
                    g.Key.KpiId,
                    g.Key.KpiYearPlanId,
                    g.Key.Year,
                    g.Key.Priority,
                    MaxStart = g.Max(x => x.per.StartDate)
                };

            // Step 3b: join to get the *record* that has that MaxStart so we can read its StatusCode
            var latestWithStatus =
                from lf in latestFactPerKpi
                join f in _db.KpiFacts on new { lf.KpiId, PlanId = lf.KpiYearPlanId } equals new { f.KpiId, PlanId = f.KpiYearPlanId }
                join per in _db.DimPeriods on f.PeriodId equals per.PeriodId
                where per.Year == lf.Year && per.StartDate == lf.MaxStart && f.StatusCode != null
                select new
                {
                    lf.KpiId,
                    lf.Priority,
                    LatestStatus = f.StatusCode
                };

            // Step 4: keep only those whose latest status is red, then project with KPI info
            var redLatest =
                from lw in latestWithStatus
                where redCodes.Contains(lw.LatestStatus.ToLower())
                join k in _db.DimKpis on lw.KpiId equals k.KpiId
                join o in _db.DimObjectives on k.ObjectiveId equals o.ObjectiveId into gobj
                from o in gobj.DefaultIfEmpty()
                join p in _db.DimPillars on k.PillarId equals p.PillarId into gpil
                from p in gpil.DefaultIfEmpty()
                select new
                {
                    lw.KpiId,
                    KpiName = k.KpiName,
                    KpiCode = k.KpiCode,
                    lw.Priority,
                    PillarCode = p != null ? p.PillarCode : "",
                    PillarName = p != null ? p.PillarName : "",
                    ObjectiveCode = o != null ? o.ObjectiveCode : "",
                    ObjectiveName = o != null ? o.ObjectiveName : ""
                };

            var list = await redLatest
                .AsNoTracking()
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.PillarCode)
                .ThenBy(x => x.ObjectiveCode)
                .ThenBy(x => x.KpiCode)
                .ToListAsync();

            // Build the payload your view expects
            var result = list
                .Select(x =>
                {
                    string pillCode = (x.PillarCode ?? "").Trim();
                    string objCode = (x.ObjectiveCode ?? "").Trim();
                    string kpiCode = (x.KpiCode ?? "").Trim();

                    string pillName = (x.PillarName ?? "").Trim();
                    string objName = (x.ObjectiveName ?? "").Trim();

                    var line1 = $"KPI Code: {pillCode}.{objCode} {kpiCode}";
                    var line2 = string.IsNullOrEmpty(pillName) ? "" : $"Pillar: {pillName}";
                    var line3 = string.IsNullOrEmpty(objName) ? "" : $"Objective: {objName}";
                    var subtitle = string.Join("\n", new[] { line1, line2, line3 }
                        .Where(s => !string.IsNullOrWhiteSpace(s)));

                    return new
                    {
                        kpiId = x.KpiId,
                        name = x.KpiName ?? "-",
                        code = subtitle,
                        priority = x.Priority
                    };
                })
                .ToList();


            return Json(result);
        }

        // Single KPI payload (same shape as Dashboard’s summary)
        [HttpGet]
        public async Task<IActionResult> GetKpiPresentation(decimal kpiId)
        {
            // latest active plan for this KPI (with Period)
            var plan = await _db.KpiYearPlans
                .Include(p => p.Period)
                .AsNoTracking()
                .Where(p => p.KpiId == kpiId && p.IsActive == 1 && p.Period != null)
                .OrderByDescending(p => p.KpiYearPlanId)
                .FirstOrDefaultAsync();

            if (plan == null || plan.Period == null)
            {
                return Json(new
                {
                    meta = new { owner = "—", editor = "—", valueType = "—", unit = "—", priority = (int?)null, statusLabel = "—", statusColor = "" },
                    chart = new { labels = Array.Empty<string>(), actual = Array.Empty<decimal?>(), target = Array.Empty<decimal?>(), forecast = Array.Empty<decimal?>(), yearTargets = Array.Empty<object>() },
                    table = Array.Empty<object>()
                });
            }

            int planYear = plan.Period.Year;

            // Facts for that plan year
            var facts = await _db.KpiFacts
    .Include(f => f.Period)
    .AsNoTracking()
    .Where(f => f.KpiId == kpiId
             && f.IsActive == 1
             && f.KpiYearPlanId == plan.KpiYearPlanId
             && f.Period != null
             && f.Period.Year == planYear)
    .OrderBy(f => f.Period!.StartDate)   // <-- use StartDate for natural order
    .ToListAsync();

            static string LabelFor(DimPeriod p)
            {
                if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
                if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
                return p.Year.ToString();
            }

            var labels = facts.Select(f => LabelFor(f.Period!)).ToList();
            var actual = facts.Select(f => (decimal?)f.ActualValue).ToList();
            var target = facts.Select(f => (decimal?)f.TargetValue).ToList();
            var forecast = facts.Select(f => (decimal?)f.ForecastValue).ToList();

            // find the most recent fact that actually has a status
            var lastWithStatus = facts.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.StatusCode));
            string? latestStatusCode = lastWithStatus?.StatusCode;

            (string label, string color) status = latestStatusCode?.Trim().ToLowerInvariant() switch
            {
                "green" => ("Ok", "#28a745"),
                "red" => ("Needs Attention", "#dc3545"),
                "orange" => ("Catching Up", "#fd7e14"),
                "blue" => ("Data Missing", "#0d6efd"),
                "conforme" => ("Ok", "#28a745"),
                "ecart" => ("Needs Attention", "#dc3545"),
                "rattrapage" => ("Catching Up", "#fd7e14"),
                "attente" => ("Data Missing", "#0d6efd"),
                _ => ("—", "")
            };

            // five-year targets (bars to the right)
            var fy = await _db.KpiFiveYearTargets
                .AsNoTracking()
                .Where(t => t.KpiId == kpiId && t.IsActive == 1)
                .OrderByDescending(t => t.BaseYear)
                .FirstOrDefaultAsync();

            var yearTargets = new List<object>();
            if (fy != null)
            {
                void Add(int off, decimal? v) { if (v.HasValue) yearTargets.Add(new { year = fy.BaseYear + off, value = v.Value }); }
                Add(0, fy.Period1Value); Add(1, fy.Period2Value); Add(2, fy.Period3Value); Add(3, fy.Period4Value); Add(4, fy.Period5Value);
            }

            string fmt(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "—";
            var table = facts.Select(f => new
            {
                id = f.KpiFactId,
                period = LabelFor(f.Period!),
                startDate = fmt(f.Period!.StartDate),
                endDate = fmt(f.Period!.EndDate),
                actual = (decimal?)f.ActualValue,
                target = (decimal?)f.TargetValue,
                forecast = (decimal?)f.ForecastValue,
                statusCode = f.StatusCode ?? "",
                lastBy = string.IsNullOrWhiteSpace(f.LastChangedBy) ? "-" : f.LastChangedBy
            }).ToList();

            var kpi = await _db.DimKpis.AsNoTracking().FirstOrDefaultAsync(x => x.KpiId == kpiId);
            var meta = new
            {
                title = kpi?.KpiName ?? "—",
                code = kpi?.KpiCode ?? "—",
                owner = plan.Owner ?? "—",
                editor = plan.Editor ?? "—",
                valueType = string.IsNullOrWhiteSpace(plan.Frequency) ? "—" : plan.Frequency,
                unit = string.IsNullOrWhiteSpace(plan.Unit) ? "—" : plan.Unit,
                priority = plan.Priority,
                statusLabel = status.label,
                statusColor = status.color
            };

            return Json(new
            {
                meta,
                chart = new { labels, actual, target, forecast, yearTargets },
                table
            });
        }
        // ---------- Actions (modals/partials only) ----------
        [HttpGet]
        public async Task<IActionResult> ActionForm(decimal kpiId)
        {
            var kpi = await _db.DimKpis.AsNoTracking().FirstOrDefaultAsync(x => x.KpiId == kpiId);
            if (kpi == null) return NotFound();

            var vm = new KpiAction
            {
                KpiId = kpiId,
                AssignedAt = DateTime.UtcNow,
                ExtensionCount = 0,
                StatusCode = "todo"
            };
            ViewBag.KpiTitle = $"{kpi.KpiCode} — {kpi.KpiName}";
            return PartialView("_ActionForm", vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAction(KpiAction vm)
        {
            if (!ModelState.IsValid) return BadRequest("Invalid data.");
            vm.CreatedBy = User?.Identity?.Name ?? "system";
            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedBy = vm.CreatedBy;
            vm.LastChangedDate = vm.CreatedDate;

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        // [HttpGet]
        // public async Task<IActionResult> ActionsList(decimal kpiId)
        // {
        //     var list = await _db.KpiActions
        //         .AsNoTracking()
        //         .Where(a => a.KpiId == kpiId)
        //         .OrderBy(a => a.StatusCode)
        //         .ThenBy(a => a.DueDate)
        //         .ToListAsync();

        //     ViewBag.KpiId = kpiId;
        //     return PartialView("_ActionsList", list);
        // }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveDeadline(decimal actionId, DateTime newDueDate, string? reason)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
            if (act == null) return NotFound();

            if (act.ExtensionCount >= 3)
                return BadRequest("Max 3 extensions reached.");

            _db.KpiActionDeadlineHistories.Add(new KpiActionDeadlineHistory
            {
                ActionId = act.ActionId,
                OldDueDate = act.DueDate,
                NewDueDate = newDueDate,
                ChangedAt = DateTime.UtcNow,
                ChangedBy = User?.Identity?.Name ?? "system",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            });

            act.DueDate = newDueDate;
            act.ExtensionCount = (short)(act.ExtensionCount + 1);
            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStatus(decimal actionId, string statusCode)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
            if (act == null) return NotFound();

            act.StatusCode = (statusCode ?? "todo").Trim().ToLowerInvariant();
            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();
            return Ok(new { ok = true });
        }

        // Final "Decisions" board for the current run (three columns by status)
        // [HttpPost]
        // public async Task<IActionResult> DecisionsBoard([FromBody] decimal[] kpiIds)
        // {
        //     var actions = await _db.KpiActions
        //         .AsNoTracking()
        //         .Where(a => kpiIds.Contains(a.KpiId))
        //         .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
        //         .ToListAsync();

        //     var todo = actions.Where(a => a.StatusCode == "todo").ToList();
        //     var prog = actions.Where(a => a.StatusCode == "inprogress").ToList();
        //     var done = actions.Where(a => a.StatusCode == "done").ToList();

        //     ViewBag.Total = actions.Count;
        //     return PartialView("_DecisionsBoard", (todo, prog, done));
        // }
        // ---------- HTML (no partials, no JSON) ----------

        [HttpGet]
        public async Task<IActionResult> ActionsListHtml(decimal kpiId)
        {
            var items = await _db.KpiActions
                .AsNoTracking()
                .Where(a => a.KpiId == kpiId)
                .OrderBy(a => a.StatusCode)
                .ThenBy(a => a.DueDate)
                .ToListAsync();

            static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
            static string Fmt(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

            var sb = new StringBuilder();

            if (items.Count == 0)
            {
                sb.Append("<div class='text-muted small'>No actions yet for this KPI.</div>");
                return Content(sb.ToString(), "text/html");
            }

            foreach (var a in items)
            {
                var sTodo = a.StatusCode?.Equals("todo", StringComparison.OrdinalIgnoreCase) == true ? "selected" : "";
                var sProg = a.StatusCode?.Equals("inprogress", StringComparison.OrdinalIgnoreCase) == true ? "selected" : "";
                var sDone = a.StatusCode?.Equals("done", StringComparison.OrdinalIgnoreCase) == true ? "selected" : "";

                sb.Append(@$"
<div class='border rounded-3 p-2 mb-2 bg-white'>
  <div class='d-flex justify-content-between align-items-center'>
    <div>
      <strong>{H(a.Owner)}</strong>
      <div class='small text-muted'>Assigned: {Fmt(a.AssignedAt)}</div>
    </div>
    <div class='d-flex align-items-center gap-2'>
      <select class='form-select form-select-sm' data-action='set-status' data-id='{a.ActionId}'>
        <option value='todo' {sTodo}>To Do</option>
        <option value='inprogress' {sProg}>In Progress</option>
        <option value='done' {sDone}>Done</option>
      </select>
      <button type='button' class='btn btn-sm btn-outline-secondary' data-action='move-deadline' data-id='{a.ActionId}'>Move deadline</button>
    </div>
  </div>
  <div class='mt-2'>{H(a.Description)}</div>
  <div class='small text-muted mt-1'>Due: {Fmt(a.DueDate)} • Ext: {a.ExtensionCount}</div>
</div>");
            }

            return Content(sb.ToString(), "text/html");
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DecisionsBoardHtml([FromBody] decimal[] kpiIds)
        {
            if (kpiIds == null || kpiIds.Length == 0)
                return Content("<div class='text-muted small'>No KPIs in this run.</div>", "text/html");

            // KPI-scoped actions for the selected run
            var kpiScoped = await _db.KpiActions
                .AsNoTracking()
                .Include(a => a.Kpi)
                    .ThenInclude(k => k.Objective)
                .Include(a => a.Kpi)
                    .ThenInclude(k => k.Pillar)
                .Where(a => a.KpiId.HasValue && kpiIds.Contains(a.KpiId.Value))  // fix for nullable KpiId
                .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
                .ToListAsync();

            // General actions (no KPI) — always include
            var general = await _db.KpiActions
                .AsNoTracking()
                .Where(a => a.KpiId == null)
                .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
                .ToListAsync();

            // Merge
            var actions = kpiScoped.Concat(general).ToList();

            static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
            static string Fmt(DateTime? d) => d.HasValue ? d.Value.ToString("yyyy-MM-dd HH:mm") : "—";

            var todo = actions.Where(a => string.Equals(a.StatusCode, "todo", StringComparison.OrdinalIgnoreCase)).ToList();
            var prog = actions.Where(a => string.Equals(a.StatusCode, "inprogress", StringComparison.OrdinalIgnoreCase)).ToList();
            var done = actions.Where(a => string.Equals(a.StatusCode, "done", StringComparison.OrdinalIgnoreCase)).ToList();

            string Col(string title, string badgeClass, IEnumerable<KpiAction> items)
            {
                var sb = new StringBuilder();
                sb.Append(@$"<div class='col-md-4'>
  <h6 class='text-secondary fw-bold mb-2'>{H(title)}</h6>
  <div class='d-grid gap-2'>");

                if (!items.Any())
                {
                    sb.Append("<div class='text-muted small'>No items.</div>");
                }
                else
                {
                    foreach (var a in items)
                    {
                        // Build the KPI/general info block
                        string infoBlock;
                        if (a.KpiId == null || a.IsGeneral)
                        {
                            infoBlock = "<div class='small text-muted mt-1'><span class='badge text-bg-info me-1'>General</span>General Action</div>";
                        }
                        else
                        {
                            var kpiCode = $"{H(a.Kpi?.Pillar?.PillarCode ?? "")}.{H(a.Kpi?.Objective?.ObjectiveCode ?? "")} {H(a.Kpi?.KpiCode ?? "")}";
                            var kpiName = H(a.Kpi?.KpiName ?? "-");
                            var pillarName = H(a.Kpi?.Pillar?.PillarName ?? "");
                            var objectiveName = H(a.Kpi?.Objective?.ObjectiveName ?? "");

                            infoBlock = @$"
      <div class='small text-muted mt-1'>
        KPI: <strong>{kpiCode}</strong> — {kpiName}
        {(string.IsNullOrWhiteSpace(pillarName) ? "" : $"<div>Pillar: {pillarName}</div>")}
        {(string.IsNullOrWhiteSpace(objectiveName) ? "" : $"<div>Objective: {objectiveName}</div>")}
      </div>";
                        }

                        sb.Append(@$"
    <div class='border rounded-3 p-2 bg-white'>
      <div class='d-flex justify-content-between align-items-center'>
        <strong>{H(a.Owner)}</strong>
        <span class='badge rounded-pill {badgeClass}'>{H(title)}</span>
      </div>
      {infoBlock}
      <div class='mt-1'>Description: {H(a.Description)}</div>
      <div class='text-muted small mt-1'>Due: {Fmt(a.DueDate)}</div>
      <div class='text-muted small mt-1'>Ext: {a.ExtensionCount}</div>
    </div>");
                    }
                }

                sb.Append("</div></div>");
                return sb.ToString();
            }

            var html =
                "<div class='row g-3'>" +
                Col("To Do", "text-bg-secondary", todo) +
                Col("In Progress", "text-bg-warning", prog) +
                Col("Done", "text-bg-success", done) +
                "</div>";

            return Content(html, "text/html");
        }



    }
}