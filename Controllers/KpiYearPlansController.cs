using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class KpiYearPlansController : Controller
    {
        private readonly AppDbContext _db;
        public KpiYearPlansController(AppDbContext db) => _db = db;

        // GET: /KpiYearPlans
        public async Task<IActionResult> Index(int? year)
        {
            var q = _db.KpiYearPlans
                       .Include(p => p.Kpi)
                       .Include(p => p.Period)
                       .AsNoTracking();

            if (year.HasValue)
                q = q.Where(x => x.Period != null && x.Period.Year == year.Value);

            var data = await q.OrderBy(x => x.Period!.Year)
                              .ThenBy(x => x.KpiId)
                              .ToListAsync();

            ViewBag.FilterYear = year;
            return View(data);
        }

        // GET: /KpiYearPlans/Create
        public async Task<IActionResult> Create()
        {
            await LoadKpisAsync();
            await LoadYearPeriodsAsync();
            var user = User?.Identity?.Name ?? "system";
            return View(new KpiYearPlan { IsActive = 1, CreatedBy = user, LastChangedBy = user });
        }

        // POST: /KpiYearPlans/Create
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(KpiYearPlan vm)
        {
            // enforce PeriodId is YEAR row
            bool isYearPeriod = await _db.DimPeriods
                .AnyAsync(p => p.PeriodId == vm.PeriodId && p.MonthNum == null && p.QuarterNum == null);
            if (!isYearPeriod)
                ModelState.AddModelError(nameof(vm.PeriodId), "Selected period must be a YEAR period.");

            bool kpiOk = await _db.DimKpis.AnyAsync(k => k.KpiId == vm.KpiId && k.IsActive == 1);
            if (!kpiOk)
                ModelState.AddModelError(nameof(vm.KpiId), "Select a valid, active KPI.");

            if (string.IsNullOrWhiteSpace(vm.CreatedBy))
                ModelState.AddModelError(nameof(vm.CreatedBy), "Created By is required.");
            if (string.IsNullOrWhiteSpace(vm.LastChangedBy))
                ModelState.AddModelError(nameof(vm.LastChangedBy), "Last Changed By is required.");

            if (!ModelState.IsValid)
            {
                await LoadKpisAsync(vm.KpiId);
                await LoadYearPeriodsAsync((int?)vm.PeriodId);
                return View(vm);
            }

            vm.CreatedDate = DateTime.UtcNow;
            _db.KpiYearPlans.Add(vm);

            try
            {
                await _db.SaveChangesAsync();
                TempData["Msg"] = "Year plan created.";
                var yr = await _db.DimPeriods.Where(p => p.PeriodId == vm.PeriodId).Select(p => p.Year).FirstAsync();
                return RedirectToAction(nameof(Index), new { year = yr });
            }
            catch (DbUpdateException ex)
            {
                ModelState.AddModelError(string.Empty, "Error saving year plan (duplicate or DB error): " + ex.Message);
                await LoadKpisAsync(vm.KpiId);
                await LoadYearPeriodsAsync((int?)vm.PeriodId);
                return View(vm);
            }
        }

        // GET: /KpiYearPlans/Deactivate/5
        [HttpGet]
        public async Task<IActionResult> Deactivate(decimal id)
        {
            var item = await _db.KpiYearPlans
                                .Include(x => x.Kpi)
                                .Include(x => x.Period)
                                .AsNoTracking()
                                .FirstOrDefaultAsync(x => x.KpiYearPlanId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 0)
            {
                TempData["Msg"] = "Year plan is already deactivated.";
                return RedirectToAction(nameof(Index), new { year = item.Period?.Year });
            }
            return View(item);
        }

        // POST: /KpiYearPlans/Deactivate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Deactivate(decimal id, string lastChangedBy)
        {
            var item = await _db.KpiYearPlans.Include(x => x.Period).FirstOrDefaultAsync(x => x.KpiYearPlanId == id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                return View(item);
            }

            item.IsActive = 0;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Year plan for KPI {item.KpiId} ({item.Period?.Year}) set to Deactivated.";
            return RedirectToAction(nameof(Index), new { year = item.Period?.Year });
        }

        // GET: /KpiYearPlans/Activate/5
        [HttpGet]
        public async Task<IActionResult> Activate(decimal id)
        {
            var item = await _db.KpiYearPlans
                                .Include(x => x.Kpi)
                                .Include(x => x.Period)
                                .AsNoTracking()
                                .FirstOrDefaultAsync(x => x.KpiYearPlanId == id);
            if (item == null) return NotFound();
            if (item.IsActive == 1)
            {
                TempData["Msg"] = "Year plan is already active.";
                return RedirectToAction(nameof(Index), new { year = item.Period?.Year });
            }
            return View(item);
        }

        // POST: /KpiYearPlans/Activate/5
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Activate(decimal id, string lastChangedBy)
        {
            var item = await _db.KpiYearPlans.Include(x => x.Period).FirstOrDefaultAsync(x => x.KpiYearPlanId == id);
            if (item == null) return NotFound();

            if (string.IsNullOrWhiteSpace(lastChangedBy))
            {
                ModelState.AddModelError(nameof(lastChangedBy), "Last Changed By is required.");
                return View(item);
            }

            item.IsActive = 1;
            item.LastChangedBy = lastChangedBy;
            await _db.SaveChangesAsync();

            TempData["Msg"] = $"Year plan for KPI {item.KpiId} ({item.Period?.Year}) set to Active.";
            return RedirectToAction(nameof(Index), new { year = item.Period?.Year });
        }

        // helpers
        private async Task LoadKpisAsync(decimal? selected = null)
        {
            var list = await _db.DimKpis
                                .Where(k => k.IsActive == 1)
                                .OrderBy(k => k.KpiName)
                                .Select(k => new { k.KpiId, Label = k.KpiCode + " — " + k.KpiName })
                                .ToListAsync();
            ViewBag.Kpis = new SelectList(list, "KpiId", "Label", selected);
        }

        private async Task LoadYearPeriodsAsync(int? selected = null)
        {
            var list = await _db.DimPeriods
                                .Where(p => p.IsActive == 1 && p.MonthNum == null && p.QuarterNum == null)
                                .OrderBy(p => p.Year)
                                .Select(p => new { p.PeriodId, Label = "Year " + p.Year })
                                .ToListAsync();
            ViewBag.YearPeriods = new SelectList(list, "PeriodId", "Label", selected);
        }
    }
}