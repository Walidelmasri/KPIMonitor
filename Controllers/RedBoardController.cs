using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

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
    // Latest active plan per KPI (that has a Period row)
    var latestPlans = await _db.KpiYearPlans
        .Include(p => p.Period)
        .Where(p => p.IsActive == 1 && p.Period != null)
        .GroupBy(p => p.KpiId)
        .Select(g => g.OrderByDescending(x => x.KpiYearPlanId).First())
        .ToListAsync();

    var redKpiIds = new List<(decimal kpiId, string name, string code, int? priority)>();

    foreach (var plan in latestPlans)
    {
        if (plan.Period == null) continue;
        var planYear = plan.Period.Year;

        var hasRed = await _db.KpiFacts
            .Where(f => f.KpiId == plan.KpiId
                     && f.IsActive == 1
                     && f.KpiYearPlanId == plan.KpiYearPlanId
                     && f.Period != null
                     && f.Period.Year == planYear
                     && (f.StatusCode ?? "").Trim().ToLower() == "red")
            .AnyAsync();

        if (!hasRed) continue;

        // KPI + its Objective + Pillar
        var kpi = await _db.DimKpis.AsNoTracking()
            .FirstOrDefaultAsync(k => k.KpiId == plan.KpiId);
        if (kpi == null) continue;

        DimObjective? obj = null;
        DimPillar? pil = null;

        if (kpi.ObjectiveId != null)
        {
            obj = await _db.DimObjectives.AsNoTracking()
                   .FirstOrDefaultAsync(o => o.ObjectiveId == kpi.ObjectiveId);
            if (obj?.PillarId != null)
            {
                pil = await _db.DimPillars.AsNoTracking()
                       .FirstOrDefaultAsync(p => p.PillarId == obj.PillarId);
            }
        }

// Codes
string pillCode = (pil?.PillarCode ?? "").Trim();
string objCode  = (obj?.ObjectiveCode ?? "").Trim();
string kpiCode  = (kpi.KpiCode ?? "").Trim();

// Names
string pillName = (pil?.PillarName ?? "").Trim();
string objName  = (obj?.ObjectiveName ?? "").Trim();

// Line 1: KPI code with structure
string line1 = $"KPI Code: {pillCode}.{objCode} {kpiCode}";
// Line 2: Pillar name
string line2 = string.IsNullOrEmpty(pillName) ? "" : $"Pillar: {pillName}";
// Line 3: Objective name
string line3 = string.IsNullOrEmpty(objName) ? "" : $"Objective: {objName}";
// Line 4: Priority
string line4 = $"Priority: {plan.Priority}";

// Join with newlines — works because your view uses innerText (not innerHTML)
string subtitle = string.Join("\n", new[] { line1, line2, line3 }
    .Where(s => !string.IsNullOrWhiteSpace(s)));

redKpiIds.Add((plan.KpiId, kpi.KpiName ?? "-", subtitle, plan.Priority));
    }

    var ordered = redKpiIds
        .OrderBy(x => x.priority ?? int.MaxValue) // nulls last
        .ThenBy(x => x.name)
        .Select(x => new { kpiId = x.kpiId, name = x.name, code = x.code, priority = x.priority })
        .ToList();

    return Json(ordered);
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
                .OrderBy(f => f.Period!.MonthNum ?? 0)
                .ThenBy(f => f.Period!.QuarterNum ?? 0)
                .ToListAsync();

            static string LabelFor(DimPeriod p)
            {
                if (p.MonthNum.HasValue) return $"{p.Year} — {new DateTime(p.Year, p.MonthNum.Value, 1):MMM}";
                if (p.QuarterNum.HasValue) return $"{p.Year} — Q{p.QuarterNum.Value}";
                return p.Year.ToString();
            }

            var labels   = facts.Select(f => LabelFor(f.Period!)).ToList();
            var actual   = facts.Select(f => (decimal?)f.ActualValue).ToList();
            var target   = facts.Select(f => (decimal?)f.TargetValue).ToList();
            var forecast = facts.Select(f => (decimal?)f.ForecastValue).ToList();

            var lastFact = facts.LastOrDefault();
            (string label, string color) status = (lastFact?.StatusCode ?? "").Trim().ToLower() switch
            {
                "green" => ("Ok", "#28a745"), 
                "red" => ("Needs Attention", "#dc3545"), 
                "orange" => ("Catching Up", "#fd7e14"), 
                "blue" => ("Data Missing", "#0d6efd"),
                "conforme" => ("Ok", "#28a745"), 
                "ecart" => ("Needs Attention", "#dc3545"), 
                "rattrapage"=> ("Catching Up", "#fd7e14"), 
                "attente"=> ("Data Missing", "#0d6efd"),
                _        => ("—",         "")
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

[HttpGet]
public async Task<IActionResult> ActionsList(decimal kpiId)
{
    var list = await _db.KpiActions
        .AsNoTracking()
        .Where(a => a.KpiId == kpiId)
        .OrderBy(a => a.StatusCode)
        .ThenBy(a => a.DueDate)
        .ToListAsync();

    ViewBag.KpiId = kpiId;
    return PartialView("_ActionsList", list);
}

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
[HttpPost]
public async Task<IActionResult> DecisionsBoard([FromBody] decimal[] kpiIds)
{
    var actions = await _db.KpiActions
        .AsNoTracking()
        .Where(a => kpiIds.Contains(a.KpiId))
        .OrderBy(a => a.StatusCode).ThenBy(a => a.DueDate)
        .ToListAsync();

    var todo = actions.Where(a => a.StatusCode == "todo").ToList();
    var prog = actions.Where(a => a.StatusCode == "inprogress").ToList();
    var done = actions.Where(a => a.StatusCode == "done").ToList();

    ViewBag.Total = actions.Count;
    return PartialView("_DecisionsBoard", (todo, prog, done));
}
    }
}