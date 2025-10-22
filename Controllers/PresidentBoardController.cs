using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using System.Text;
using System.Net;

namespace KPIMonitor.Controllers
{
    public class PresidentBoardController : Controller
    {
        private readonly AppDbContext _db;
        public PresidentBoardController(AppDbContext db) { _db = db; }

        // Page
        [HttpGet]
        public IActionResult Index() => View();

        // List ALL KPI ids (latest active plan per KPI), ordered by priority asc then code
        [HttpGet]
        public async Task<IActionResult> GetKpiIds()
        {
            // latest plan id per KPI
            var latestIds =
                from p in _db.KpiYearPlans
                where p.IsActive == 1 && p.Period != null
                group p by p.KpiId into g
                select new
                {
                    KpiId = g.Key,
                    MaxPlanId = g.Max(x => x.KpiYearPlanId)
                };

            // join back to the actual plan row to get the year & priority
            var latestPlans =
                from lid in latestIds
                join p in _db.KpiYearPlans on
                    new { lid.KpiId, PlanId = lid.MaxPlanId }
                    equals new { p.KpiId, PlanId = p.KpiYearPlanId }
                select new
                {
                    p.KpiId,
                    p.KpiYearPlanId,
                    Year = p.Period!.Year,
                    p.Priority
                };

            // pull KPI header (pillar/objective/code/name)
            var allLatest =
                from lp in latestPlans
                join k in _db.DimKpis on lp.KpiId equals k.KpiId
                join o in _db.DimObjectives on k.ObjectiveId equals o.ObjectiveId into gobj
                from o in gobj.DefaultIfEmpty()
                join pl in _db.DimPillars on k.PillarId equals pl.PillarId into gpil
                from pl in gpil.DefaultIfEmpty()
                select new
                {
                    lp.KpiId,
                    KpiName = k.KpiName,
                    KpiCode = k.KpiCode,
                    lp.Priority,
                    PillarCode = pl != null ? pl.PillarCode : "",
                    PillarName = pl != null ? pl.PillarName : "",
                    ObjectiveCode = o != null ? o.ObjectiveCode : "",
                    ObjectiveName = o != null ? o.ObjectiveName : ""
                };

            var list = await allLatest
                .AsNoTracking()
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.PillarCode)
                .ThenBy(x => x.ObjectiveCode)
                .ThenBy(x => x.KpiCode)
                .ToListAsync();

            var result = list.Select(x =>
            {
                string pillCode = (x.PillarCode ?? "").Trim();
                string objCode = (x.ObjectiveCode ?? "").Trim();
                string kpiCode = (x.KpiCode ?? "").Trim();

                string pillName = (x.PillarName ?? "").Trim();
                string objName = (x.ObjectiveName ?? "").Trim();

                var line1 = $"KPI Code: {pillCode}.{objCode} {kpiCode}";
                var line2 = string.IsNullOrEmpty(pillName) ? "" : $"Pillar: {pillName}";
                var line3 = string.IsNullOrEmpty(objName) ? "" : $"Objective: {objName}";
                var subtitle = string.Join("\n", new[] { line1, line2, line3 }.Where(s => !string.IsNullOrWhiteSpace(s)));

                return new
                {
                    kpiId = x.KpiId,
                    name = x.KpiName ?? "-",
                    code = subtitle,
                    priority = x.Priority
                };
            }).ToList();

            return Json(result);
        }

        // Single KPI payload (same shape as RedBoard’s summary)
        [HttpGet]
        public async Task<IActionResult> GetKpiPresentation(decimal kpiId)
        {
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

            var facts = await _db.KpiFacts
                .Include(f => f.Period)
                .AsNoTracking()
                .Where(f => f.KpiId == kpiId
                         && f.IsActive == 1
                         && f.KpiYearPlanId == plan.KpiYearPlanId
                         && f.Period != null
                         && f.Period.Year == planYear)
                .OrderBy(f => f.Period!.StartDate)
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

            var lastWithStatus = facts.LastOrDefault(f => !string.IsNullOrWhiteSpace(f.StatusCode));
            string? latestStatusCode = lastWithStatus?.StatusCode;

            (string label, string color) status = (latestStatusCode ?? "").Trim().ToLowerInvariant() switch
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

        // HTML list of actions (same rendering shape as RedBoard so the JS can inject it)
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

        // Optional: decisions board HTML (same shape; reuse from RedBoard if you want that slide later)
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DecisionsBoardHtml([FromBody] decimal[] kpiIds)
        {
            if (kpiIds == null || kpiIds.Length == 0)
                return Content("<div class='text-muted small'>No KPIs in this run.</div>", "text/html");

            var kpiScoped = await _db.KpiActions
                .AsNoTracking()
                .Include(a => a.Kpi).ThenInclude(k => k.Objective)
                .Include(a => a.Kpi).ThenInclude(k => k.Pillar)
                .Where(a => a.KpiId.HasValue && kpiIds.Contains(a.KpiId.Value))
                .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
                .ToListAsync();

            var general = await _db.KpiActions
                .AsNoTracking()
                .Where(a => a.KpiId == null)
                .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
                .ToListAsync();

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
