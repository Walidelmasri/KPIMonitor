using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class ObjectivesController : Controller
    {
        private readonly AppDbContext _db;
        public ObjectivesController(AppDbContext db) => _db = db;

        // GET: /Objectives
        public async Task<IActionResult> Index()
        {
            var data = await _db.DimObjectives
                                .Include(o => o.Pillar)
                                .OrderBy(o => o.ObjectiveId)
                                .ToListAsync();
            return View(data);
        }

        // GET: /Objectives/Create
        public async Task<IActionResult> Create()
        {
            await LoadPillarsAsync();
            var user = User?.Identity?.Name ?? "system";
            return View(new DimObjective { IsActive = 1, CreatedBy = user, LastChangedBy = user });
        }

        // POST: /Objectives/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(DimObjective vm)
        {
            if (!ModelState.IsValid)
            {
                await LoadPillarsAsync();
                return View(vm);
            }

            try
            {
                vm.CreatedDate = DateTime.UtcNow;
                _db.DimObjectives.Add(vm);
                await _db.SaveChangesAsync();

                TempData["Msg"] = $"Objective \"{vm.ObjectiveName}\" created.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "Error creating objective: " + ex.Message);
                await LoadPillarsAsync();
                return View(vm);
            }
        }

        // GET: /Objectives/Inactivate/5
        [HttpGet]
        public async Task<IActionResult> Inactivate(decimal id)
        {
            var item = await _db.DimObjectives.Include(o => o.Pillar).FirstOrDefaultAsync(o => o.ObjectiveId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 0)
            {
                TempData["Msg"] = "Objective is already inactive.";
                return RedirectToAction(nameof(Index));
            }
            return View(item);
        }

        // POST: /Objectives/Inactivate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Inactivate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimObjectives.FindAsync(id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                // reload for view
                item.LastChangedBy = lastChangedBy;
                item.Pillar = await _db.DimPillars.FindAsync(item.PillarId);
                return View(item);
            }

            item.IsActive = 0;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Objective \"{item.ObjectiveName}\" set to inactive.";
            return RedirectToAction(nameof(Index));
        }

        // GET: /Objectives/Activate/5
        [HttpGet]
        public async Task<IActionResult> Activate(decimal id)
        {
            var item = await _db.DimObjectives.Include(o => o.Pillar).FirstOrDefaultAsync(o => o.ObjectiveId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 1)
            {
                TempData["Msg"] = "Objective is already active.";
                return RedirectToAction(nameof(Index));
            }
            return View(item);
        }

        // POST: /Objectives/Activate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(decimal id, string lastChangedBy)
        {
            var item = await _db.DimObjectives.FindAsync(id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                // reload for view
                item.LastChangedBy = lastChangedBy;
                item.Pillar = await _db.DimPillars.FindAsync(item.PillarId);
                return View(item);
            }

            item.IsActive = 1;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Objective \"{item.ObjectiveName}\" set to active.";
            return RedirectToAction(nameof(Index));
        }

        private async Task LoadPillarsAsync(decimal? selected = null)
        {
            var pillars = await _db.DimPillars
                                   .Where(p => p.IsActive == 1)   // if empty, comment this line to test
                                   .OrderBy(p => p.PillarName)
                                   .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
                                   .ToListAsync();

            ViewBag.Pillars = new SelectList(pillars, "PillarId", "Name", selected);
        }

    }
}
