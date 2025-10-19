using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using System.Text;
using System.Net;
using System.Linq;
using System;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace KPIMonitor.Controllers
{
    public class RedBoardController : Controller
    {
        private readonly AppDbContext _db;
        public RedBoardController(AppDbContext db) { _db = db; }

        // Page
        [HttpGet]
        public IActionResult Index() => View();

        // List of KPI ids that are "red" based on the LATEST status in the current active plan-year
        [HttpGet]
        public async Task<IActionResult> GetRedKpiIds()
        {
            var redCodes = new[] { "red", "ecart" }; // normalize to lowercase

            // Step 1: latest plan id per KPI (active & with a period)
            var latestIds =
                from p in _db.KpiYearPlans
                where p.IsActive == 1 && p.Period != null
                group p by p.KpiId into g
                select new
                {
                    KpiId = g.Key,
                    MaxPlanId = g.Max(x => x.KpiYearPlanId)
                };

            // Step 2: join back to plan row to get the plan-year and priority
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

            // Step 3a: For each KPI/plan-year, find the *latest* fact by PeriodId (robust if StartDate is null)
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
                    MaxPeriodId = g.Max(x => x.per.PeriodId)
                };

            // Step 3b: Join back to get that record so we can read its StatusCode
            var latestWithStatus =
                from lf in latestFactPerKpi
                join f in _db.KpiFacts on new { lf.KpiId, PlanId = lf.KpiYearPlanId } equals new { f.KpiId, PlanId = f.KpiYearPlanId }
                join per in _db.DimPeriods on f.PeriodId equals per.PeriodId
                where per.Year == lf.Year && per.PeriodId == lf.MaxPeriodId && f.StatusCode != null
                select new
                {
                    lf.KpiId,
                    lf.Priority,
                    LatestStatus = f.StatusCode
                };

            // Step 4: keep only those whose latest status is red, then project with KPI info
            var query =
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

            var list = await query
                .OrderBy(x => x.Priority)
                .ThenBy(x => x.PillarCode)
                .ThenBy(x => x.ObjectiveCode)
                .ThenBy(x => x.KpiCode)
                .ToListAsync();

            return Json(list);
        }

        // ---------- Actions HTML helpers (unchanged) ----------

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
                .Where(a => a.KpiId.HasValue && kpiIds.Contains(a.KpiId.Value))
                .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
                .ToListAsync();

            // General actions (no KPI)
            var general = await _db.KpiActions
                .AsNoTracking()
                .Where(a => a.KpiId == null || a.IsGeneral)
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
