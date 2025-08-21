using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class KpisController : Controller
    {
        private readonly AppDbContext _db;
        public KpisController(AppDbContext db) => _db = db;

        // GET: /Kpis
        // public async Task<IActionResult> Index()
        // {
        //     var data = await _db.DimKpis
        //                         .Include(k => k.Pillar)
        //                         .Include(k => k.Objective)
        //                         .OrderBy(k => k.KpiId)
        //                         .ToListAsync();
        //     return View(data);
        // }
// GET: /Kpis
public async Task<IActionResult> Index(decimal? pillarId)
{
    // Base query
    var q = _db.DimKpis
               .Include(k => k.Pillar)
               .Include(k => k.Objective)
               .AsNoTracking();

    // Optional filter
    if (pillarId.HasValue)
        q = q.Where(k => k.PillarId == pillarId.Value);

var data = await q
    .OrderBy(k => k.Pillar!.PillarCode)          // 1) by Pillar
    .ThenBy(k => k.Objective!.ObjectiveCode)     // 2) then by Objective code
    .ThenBy(k => k.KpiCode ?? "")                // 3) finally by KPI code (stable)
    .ToListAsync();

    // Dropdown items: ordered by PillarCode (as requested)
    ViewBag.Pillars = new SelectList(
        await _db.DimPillars
                 .AsNoTracking()
                 .OrderBy(p => p.PillarCode)
                 .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
                 .ToListAsync(),
        "PillarId", "Name", pillarId
    );

    // For showing the Reset button when filtering
    ViewBag.CurrentPillarId = pillarId;

    return View(data);
}
        // GET: /Kpis/Create
        public async Task<IActionResult> Create()
        {
            await LoadPillarsAsync();
            // start with an empty list; UI will fetch after pillar is chosen
            ViewBag.Objectives = new SelectList(Enumerable.Empty<SelectListItem>(), "Value", "Text");
            var user = User?.Identity?.Name ?? "system";
            return View(new DimKpi { IsActive = 1, CreatedBy = user, LastChangedBy = user });
        }


        // POST: /Kpis/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(DimKpi vm)
        {
            // Validate objective belongs to pillar
            var objective = await _db.DimObjectives.AsNoTracking().FirstOrDefaultAsync(o => o.ObjectiveId == vm.ObjectiveId);
            if (objective == null)
            {
                ModelState.AddModelError(nameof(vm.ObjectiveId), "Objective not found.");
            }
            else if (objective.PillarId != vm.PillarId)
            {
                ModelState.AddModelError(nameof(vm.ObjectiveId), "Selected objective does not belong to the chosen pillar.");
            }

            if (!ModelState.IsValid)
            {
                await LoadPillarsAsync(vm.PillarId);
                return View(vm);
            }

            try
            {
                vm.CreatedDate = DateTime.UtcNow;
                _db.DimKpis.Add(vm);
                await _db.SaveChangesAsync();

                TempData["Msg"] = $"KPI \"{vm.KpiName}\" created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "Error creating KPI: " + ex.Message);
                await LoadPillarsAsync(vm.PillarId);
                return View(vm);
            }
        }

        // GET: /Kpis/Inactivate/5
        [HttpGet]
        public async Task<IActionResult> Inactivate(decimal id)
        {
            var item = await _db.DimKpis.Include(k => k.Pillar)
                                        .Include(k => k.Objective)
                                        .FirstOrDefaultAsync(k => k.KpiId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 0)
            {
                TempData["Msg"] = "KPI is already inactive.";
                return RedirectToAction(nameof(Index));
            }
            return View(item);
        }

        // POST: /Kpis/Inactivate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Inactivate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimKpis.FindAsync(id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                item.LastChangedBy = lastChangedBy;
                item.Pillar = await _db.DimPillars.FindAsync(item.PillarId);
                item.Objective = await _db.DimObjectives.FindAsync(item.ObjectiveId);
                return View(item);
            }

            item.IsActive = 0;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"KPI \"{item.KpiName}\" set to inactive.";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Kpis/Activate/5
        [HttpGet]
        public async Task<IActionResult> Activate(decimal id)
        {
            var item = await _db.DimKpis.Include(k => k.Pillar)
                                        .Include(k => k.Objective)
                                        .FirstOrDefaultAsync(k => k.KpiId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 1)
            {
                TempData["Msg"] = "KPI is already active.";
                return RedirectToAction(nameof(Index));
            }
            return View(item);
        }

        // POST: /Kpis/Activate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimKpis.FindAsync(id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                item.LastChangedBy = lastChangedBy;
                item.Pillar = await _db.DimPillars.FindAsync(item.PillarId);
                item.Objective = await _db.DimObjectives.FindAsync(item.ObjectiveId);
                return View(item);
            }

            item.IsActive = 1;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"KPI \"{item.KpiName}\" set to active.";
            return RedirectToAction(nameof(Index));
        }

        private async Task LoadPillarsAsync(decimal? selected = null)
        {
            var pillars = await _db.DimPillars
                                   .Where(p => p.IsActive == 1)
                                   .OrderBy(p => p.PillarCode)
                                   .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
                                   .ToListAsync();
            ViewBag.Pillars = new SelectList(pillars, "PillarId", "Name", selected);
        }

        // GET: /Kpis/GetObjectives?pillarId=123
        [HttpGet]
        public async Task<IActionResult> GetObjectives(decimal pillarId)
        {
            var objs = await _db.DimObjectives
                                .AsNoTracking()
                                .Where(o => o.IsActive == 1 && o.PillarId == pillarId)
                                .OrderBy(o => o.ObjectiveCode)
                                .Select(o => new { id = o.ObjectiveId, name = o.ObjectiveCode + " — " + o.ObjectiveName })
                                .ToListAsync();
            return Json(objs);
        }

    }
}
