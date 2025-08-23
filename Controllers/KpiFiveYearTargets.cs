using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class KpiFiveYearTargetsController : Controller
    {
        private readonly AppDbContext _db;
        public KpiFiveYearTargetsController(AppDbContext db) => _db = db;

        // GET: /KpiFiveYearTargets
public async Task<IActionResult> Index(decimal? pillarId, decimal? objectiveId)
{
    var q = _db.KpiFiveYearTargets
        .Include(t => t.Kpi)
            .ThenInclude(k => k.Pillar)
        .Include(t => t.Kpi)
            .ThenInclude(k => k.Objective)
        .AsNoTracking();

    if (pillarId.HasValue)
        q = q.Where(t => t.Kpi != null && t.Kpi.PillarId == pillarId.Value);

    if (objectiveId.HasValue)
        q = q.Where(t => t.Kpi != null && t.Kpi.ObjectiveId == objectiveId.Value);

    var data = await q
        .OrderBy(t => t.Kpi!.Pillar!.PillarCode)         // pillar first
        .ThenBy(t => t.Kpi!.Objective!.ObjectiveCode)     // then objective
        .ThenBy(t => t.Kpi!.KpiCode)                      // then KPI
        .ThenByDescending(t => t.BaseYear)                // newest base year first
        .ToListAsync();

    // Pillar dropdown (ordered by code)
    ViewBag.Pillars = new SelectList(
        await _db.DimPillars
            .AsNoTracking()
            .OrderBy(p => p.PillarCode)
            .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
            .ToListAsync(),
        "PillarId", "Name", pillarId
    );

    // Objective dropdown (only when a pillar is chosen)
    if (pillarId.HasValue)
    {
        ViewBag.Objectives = new SelectList(
            await _db.DimObjectives
                .AsNoTracking()
                .Where(o => o.PillarId == pillarId.Value && o.IsActive == 1)
                .OrderBy(o => o.ObjectiveCode)
                .Select(o => new { o.ObjectiveId, Name = o.ObjectiveCode + " — " + o.ObjectiveName })
                .ToListAsync(),
            "ObjectiveId", "Name", objectiveId
        );
    }
    else
    {
        ViewBag.Objectives = new SelectList(Enumerable.Empty<SelectListItem>(), "Value", "Text");
    }

    ViewBag.CurrentPillarId = pillarId;
    ViewBag.CurrentObjectiveId = objectiveId;

    return View(data);
}

        // GET: /KpiFiveYearTargets/Create
        [HttpGet]
public async Task<IActionResult> Create()
{
    await LoadPillarsAsync(); // first dropdown only
    ViewBag.Objectives = new SelectList(Enumerable.Empty<SelectListItem>(), "Value", "Text");
    ViewBag.Kpis       = new SelectList(Enumerable.Empty<SelectListItem>(), "Value", "Text");

    var user = User?.Identity?.Name ?? "system";
    return View(new KpiFiveYearTarget {
        IsActive = 1,
        CreatedBy = user,
        LastChangedBy = user
    });
}

        // POST: /KpiFiveYearTargets/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(KpiFiveYearTarget vm)
        {
            if (vm.KpiId <= 0)
                ModelState.AddModelError(nameof(vm.KpiId), "KPI is required.");
            if (vm.BaseYear < 1900 || vm.BaseYear > 3000)
                ModelState.AddModelError(nameof(vm.BaseYear), "Base year is invalid.");
            if (string.IsNullOrWhiteSpace(vm.CreatedBy))
                ModelState.AddModelError(nameof(vm.CreatedBy), "Created By is required.");
            if (string.IsNullOrWhiteSpace(vm.LastChangedBy))
                ModelState.AddModelError(nameof(vm.LastChangedBy), "Last Changed By is required.");

            if (!ModelState.IsValid)
            {
                await LoadKpisAsync(vm.KpiId);
                return View(vm);
            }

            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedDate = DateTime.UtcNow;

            _db.KpiFiveYearTargets.Add(vm);
            await _db.SaveChangesAsync();

            TempData["Msg"] = "Five-year target created.";
            return RedirectToAction(nameof(Index));
        }

        // GET: /KpiFiveYearTargets/Edit/5
        [HttpGet]
        public async Task<IActionResult> Edit(decimal id)
        {
            var item = await _db.KpiFiveYearTargets.FindAsync(id);
            if (item == null) return NotFound();

            await LoadKpisAsync(item.KpiId);
            return View(item);
        }

        // POST: /KpiFiveYearTargets/Edit/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(decimal id, KpiFiveYearTarget vm)
        {
            if (id != vm.KpiFiveYearTargetId) return BadRequest();

            if (vm.KpiId <= 0)
                ModelState.AddModelError(nameof(vm.KpiId), "KPI is required.");
            if (vm.BaseYear < 1900 || vm.BaseYear > 3000)
                ModelState.AddModelError(nameof(vm.BaseYear), "Base year is invalid.");
            if (string.IsNullOrWhiteSpace(vm.LastChangedBy))
                ModelState.AddModelError(nameof(vm.LastChangedBy), "Last Changed By is required.");

            if (!ModelState.IsValid)
            {
                await LoadKpisAsync(vm.KpiId);
                return View(vm);
            }

            var item = await _db.KpiFiveYearTargets.FirstOrDefaultAsync(t => t.KpiFiveYearTargetId == id);
            if (item == null) return NotFound();

            // update fields
            item.KpiId = vm.KpiId;
            item.BaseYear = vm.BaseYear;

            item.Period1 = vm.Period1;
            item.Period2 = vm.Period2;
            item.Period3 = vm.Period3;
            item.Period4 = vm.Period4;
            item.Period5 = vm.Period5;

            item.Period1Value = vm.Period1Value;
            item.Period2Value = vm.Period2Value;
            item.Period3Value = vm.Period3Value;
            item.Period4Value = vm.Period4Value;
            item.Period5Value = vm.Period5Value;

            item.LastChangedBy = vm.LastChangedBy;
            item.IsActive = vm.IsActive;
            item.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            TempData["Msg"] = "Five-year target updated.";
            return RedirectToAction(nameof(Index));
        }

        private async Task LoadKpisAsync(decimal? selected = null)
        {
            var kpis = await _db.DimKpis
                                .Where(k => k.IsActive == 1)
                                .OrderBy(k => k.KpiCode)
                                .Select(k => new
                                {
                                    k.KpiId,
                                    Label = (k.KpiCode ?? "") + " — " + (k.KpiName ?? "")
                                })
                                .ToListAsync();

            ViewBag.Kpis = new SelectList(kpis, "KpiId", "Label", selected);
        }
        // GET: /KpiFiveYearTargets/GetObjectives?pillarId=123
[HttpGet]
public async Task<IActionResult> GetObjectives(decimal pillarId)
{
    var items = await _db.DimObjectives
        .AsNoTracking()
        .Where(o => o.IsActive == 1 && o.PillarId == pillarId)
        .OrderBy(o => o.ObjectiveCode)
        .Select(o => new { id = o.ObjectiveId, name = o.ObjectiveCode + " — " + o.ObjectiveName })
        .ToListAsync();

    return Json(items);
}

// GET: /KpiFiveYearTargets/GetKpis?objectiveId=456
[HttpGet]
public async Task<IActionResult> GetKpis(decimal objectiveId)
{
    var items = await _db.DimKpis
        .AsNoTracking()
        .Where(k => k.IsActive == 1 && k.ObjectiveId == objectiveId)
        .OrderBy(k => k.KpiCode)
        .Select(k => new { id = k.KpiId, name = (k.KpiCode ?? "") + " — " + (k.KpiName ?? "") })
        .ToListAsync();

    return Json(items);
}

// used by the Create GET to populate Pillars dropdown
private async Task LoadPillarsAsync(decimal? selected = null)
{
    var pillars = await _db.DimPillars
        .AsNoTracking()
        .Where(p => p.IsActive == 1)
        .OrderBy(p => p.PillarCode)
        .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
        .ToListAsync();

    ViewBag.Pillars = new SelectList(pillars, "PillarId", "Name", selected);
}
    }
}