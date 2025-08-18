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
        public async Task<IActionResult> Index(decimal? kpiId)
        {
            var q = _db.KpiFiveYearTargets
                       .Include(t => t.Kpi)
                       .AsNoTracking();

            if (kpiId.HasValue)
                q = q.Where(t => t.KpiId == kpiId.Value);

            // dropdown list of active KPIs
            await LoadKpisAsync(kpiId);

            var data = await q.OrderBy(t => t.Kpi!.KpiCode)
                              .ThenByDescending(t => t.BaseYear)
                              .ToListAsync();
            return View(data);
        }

        // GET: /KpiFiveYearTargets/Create
        [HttpGet]
        public async Task<IActionResult> Create()
        {
            await LoadKpisAsync();
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
    }
}