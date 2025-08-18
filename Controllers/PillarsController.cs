using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class PillarsController : Controller
    {
        private readonly AppDbContext _db;
        public PillarsController(AppDbContext db) => _db = db;

        // GET: /Pillars
        public async Task<IActionResult> Index()
        {
            var pillars = await _db.DimPillars
                                   .OrderBy(p => p.PillarId)
                                   .ToListAsync();
            return View(pillars);
        }

        // GET: /Pillars/Create
        public IActionResult Create()
        {
            return View(new DimPillar { IsActive = 1 });
        }

        // POST: /Pillars/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(DimPillar vm)
        {
            if (!ModelState.IsValid) return View(vm);

            try
            {
                vm.CreatedDate = DateTime.UtcNow;
                _db.DimPillars.Add(vm);
                await _db.SaveChangesAsync();

                TempData["Msg"] = $"Pillar \"{vm.PillarName}\" created successfully.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, "Error creating pillar: " + ex.Message);
                return View(vm);
            }
        }

        // GET: /Pillars/Inactivate/5  -> confirm page, asks who is making it inactive
        [HttpGet]
        public async Task<IActionResult> Inactivate(decimal id)
        {
            var item = await _db.DimPillars.FindAsync(id);
            if (item == null) return NotFound();
            if (item.IsActive == 0)
            {
                TempData["Msg"] = "Pillar is already inactive.";
                return RedirectToAction(nameof(Index));
            }
            return View(item);
        }

        // POST: /Pillars/Inactivate/5  -> actually sets IsActive=0
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Inactivate(decimal id, string lastChangedBy)
        {
            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
            }

            var item = await _db.DimPillars.FindAsync(id);
            if (item == null) return NotFound();

            if (!ModelState.IsValid)
            {
                // re-show the confirm page with validation error
                item.LastChangedBy = lastChangedBy;
                return View(item);
            }

            item.IsActive = 0;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Pillar \"{item.PillarName}\" set to inactive.";
            return RedirectToAction(nameof(Index));
        }
    }
}
