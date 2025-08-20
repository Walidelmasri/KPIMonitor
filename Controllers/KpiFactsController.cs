using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Controllers
{
    public class KpiFactsController : Controller
    {
        private readonly AppDbContext _db;
        public KpiFactsController(AppDbContext context) { _db = context; }

        // ===========================
        // Index 
        // ===========================
// GET: /KpiFacts
public async Task<IActionResult> Index(decimal? kpiId)
{
    var q = _db.KpiFacts
        .Include(f => f.Kpi)
        .Include(f => f.Period)
        .Include(f => f.KpiYearPlan)
        .AsNoTracking();

    if (kpiId.HasValue)
        q = q.Where(f => f.KpiId == kpiId.Value);

    var facts = await q
        .OrderBy(f => f.KpiId)
        .ThenBy(f => f.KpiYearPlanId)
        .ThenBy(f => f.PeriodId)
        .ToListAsync();

    // dropdown items ordered by KPI CODE
    var kpiList = await _db.DimKpis
        .Where(k => k.IsActive == 1)
        .AsNoTracking()
        .OrderBy(k => k.KpiCode)
        .Select(k => new { k.KpiId, Label = k.KpiCode + " — " + k.KpiName })
        .ToListAsync();

    ViewBag.Kpis = new SelectList(kpiList, "KpiId", "Label", kpiId);
    ViewBag.CurrentKpiId = kpiId;

    return View(facts);
}

        // ===========================
        // Create (GET)
        // ===========================
        public async Task<IActionResult> Create()
        {
            await LoadKpisAsync(); // first step only shows KPIs
            ViewBag.KpiYearPlanId = new SelectList(Enumerable.Empty<SelectListItem>());
            ViewBag.PeriodId      = new SelectList(Enumerable.Empty<SelectListItem>());

            var user = User?.Identity?.Name ?? "system";
            return View(new KpiFact { IsActive = 1, CreatedBy = user, LastChangedBy = user });
        }

        // ===========================
        // Create (POST)
        // ===========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(KpiFact vm)
        {
            // Load plan & period for validation
            var period = await _db.DimPeriods.AsNoTracking()
                .FirstOrDefaultAsync(p => p.PeriodId == vm.PeriodId);

            var plan = await _db.KpiYearPlans
                .Include(p => p.Period) // plan year
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.KpiYearPlanId == vm.KpiYearPlanId);

            // Basic presence checks
            if (plan == null)
                ModelState.AddModelError(nameof(vm.KpiYearPlanId), "Year plan not found.");

            if (period == null)
                ModelState.AddModelError(nameof(vm.PeriodId), "Period not found.");

            // KPI must match the plan’s KPI
            if (plan != null && vm.KpiId != plan.KpiId)
                ModelState.AddModelError(nameof(vm.KpiId), "Selected KPI does not match the KPI in the chosen year plan.");

            // Period year must match plan’s year
            if (plan?.Period != null && period != null && period.Year != plan.Period.Year)
                ModelState.AddModelError(nameof(vm.PeriodId), "Fact period year must match the plan’s year.");

            // Period type must match plan frequency
            var ftype = NormalizeFrequency(plan?.Frequency);
            if (period != null)
            {
                bool isMonth   = period.MonthNum   != null;
                bool isQuarter = period.QuarterNum != null;

                if (ftype == PlanFrequency.Monthly && !isMonth)
                    ModelState.AddModelError(nameof(vm.PeriodId), "This plan is Monthly; pick a MONTH period.");
                else if (ftype == PlanFrequency.Quarterly && !isQuarter)
                    ModelState.AddModelError(nameof(vm.PeriodId), "This plan is Quarterly; pick a QUARTER period.");
                else if (ftype == PlanFrequency.Unknown && !isMonth && !isQuarter)
                    ModelState.AddModelError(nameof(vm.PeriodId), "Pick a month or a quarter period (not a year).");
            }

            if (!ModelState.IsValid)
            {
                // Rebuild the cascading dropdowns with the user’s current choices:
                await LoadKpisAsync(vm.KpiId);
                await LoadYearPlansForKpiAsync(vm.KpiId, vm.KpiYearPlanId);
                await LoadPeriodsForPlanAsync(vm.KpiYearPlanId, vm.PeriodId);
                return View(vm);
            }

            vm.CreatedDate = DateTime.UtcNow;
            _db.KpiFacts.Add(vm);
            await _db.SaveChangesAsync();

            TempData["Msg"] = "KPI fact saved.";
            return RedirectToAction(nameof(Index));
        }

        // ===========================
        // Activate / Deactivate
        // ===========================
        [HttpGet]
        public async Task<IActionResult> Activate(decimal id)
        {
            var item = await _db.KpiFacts.FindAsync(id);
            if (item != null)
            {
                item.IsActive = 1;
                item.LastChangedBy = User?.Identity?.Name ?? "system";
                await _db.SaveChangesAsync();
            }
            TempData["Msg"] = "KPI fact activated.";
            return RedirectToAction(nameof(Index));
        }

        [HttpGet]
        public async Task<IActionResult> Deactivate(decimal id)
        {
            var item = await _db.KpiFacts.FindAsync(id);
            if (item != null)
            {
                item.IsActive = 0;
                item.LastChangedBy = User?.Identity?.Name ?? "system";
                await _db.SaveChangesAsync();
            }
            TempData["Msg"] = "KPI fact deactivated.";
            return RedirectToAction(nameof(Index));
        }

        // ===========================
        // AJAX endpoints (cascading)
        // ===========================

        // Returns active year-plans for the KPI (only plans whose Period is a YEAR row)
        // GET: /KpiFacts/GetYearPlans?kpiId=123
        [HttpGet]
        public async Task<IActionResult> GetYearPlans(decimal kpiId)
        {
            var plans = await _db.KpiYearPlans
                .Include(p => p.Period)
                .Where(p => p.IsActive == 1 &&
                            p.KpiId == kpiId &&
                            p.Period != null &&
                            p.Period.MonthNum == null &&
                            p.Period.QuarterNum == null)
                .OrderBy(p => p.Period!.Year)
                .Select(p => new
                {
                    id = p.KpiYearPlanId,
                    label = $"Year {p.Period!.Year}",
                    frequency = p.Frequency
                })
                .ToListAsync();

            return Json(plans);
        }

        // Returns periods for the plan’s year, filtered by plan frequency:
        // - Monthly  -> only months
        // - Quarterly-> only quarters
        // - Unknown/empty -> months + quarters
        // GET: /KpiFacts/GetPeriods?planId=456
        [HttpGet]
        public async Task<IActionResult> GetPeriods(decimal planId)
        {
            var planInfo = await _db.KpiYearPlans
                .Include(p => p.Period)
                .Where(p => p.KpiYearPlanId == planId)
                .Select(p => new { Year = (int?)p.Period!.Year, p.Frequency })
                .FirstOrDefaultAsync();

            if (planInfo == null || planInfo.Year == null)
                return Json(Array.Empty<object>());

            var ftype = NormalizeFrequency(planInfo.Frequency);

            var q = _db.DimPeriods.Where(p => p.IsActive == 1 && p.Year == planInfo.Year.Value);

            if (ftype == PlanFrequency.Monthly)
                q = q.Where(p => p.MonthNum != null);            // only months
            else if (ftype == PlanFrequency.Quarterly)
                q = q.Where(p => p.QuarterNum != null);          // only quarters
            else
                q = q.Where(p => p.MonthNum != null || p.QuarterNum != null); // both

            var periods = await q
                .OrderBy(p => p.QuarterNum ?? 0)
                .ThenBy(p => p.MonthNum ?? 0)
                .Select(p => new
                {
                    id = p.PeriodId,
                    label = p.MonthNum != null
                        ? $"{p.Year} — M{p.MonthNum:00} ({CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(p.MonthNum.Value)})"
                        : $"{p.Year} — Q{p.QuarterNum}"
                })
                .ToListAsync();

            return Json(periods);
        }

        // ===========================
        // Helpers (dropdown builders)
        // ===========================
        private async Task LoadKpisAsync(decimal? selected = null)
        {
            var list = await _db.DimKpis
                .Where(k => k.IsActive == 1)
                .OrderBy(k => k.KpiName)
                .Select(k => new { k.KpiId, Label = k.KpiCode + " — " + k.KpiName })
                .ToListAsync();

            ViewBag.KpiId = new SelectList(list, "KpiId", "Label", selected);
        }

        private async Task LoadYearPlansForKpiAsync(decimal kpiId, decimal? selected = null)
        {
            var list = await _db.KpiYearPlans
                .Include(p => p.Period)
                .Where(p => p.IsActive == 1 &&
                            p.KpiId == kpiId &&
                            p.Period != null &&
                            p.Period.MonthNum == null &&
                            p.Period.QuarterNum == null)
                .OrderBy(p => p.Period!.Year)
                .Select(p => new { p.KpiYearPlanId, Label = "Year " + p.Period!.Year })
                .ToListAsync();

            ViewBag.KpiYearPlanId = new SelectList(list, "KpiYearPlanId", "Label", selected);
        }

        private async Task LoadPeriodsForPlanAsync(decimal planId, decimal? selected = null)
        {
            var planInfo = await _db.KpiYearPlans
                .Include(p => p.Period)
                .Where(p => p.KpiYearPlanId == planId)
                .Select(p => new { Year = (int?)p.Period!.Year, p.Frequency })
                .FirstOrDefaultAsync();

            var list = new List<object>();
            if (planInfo?.Year != null)
            {
                var ftype = NormalizeFrequency(planInfo.Frequency);

                var q = _db.DimPeriods.Where(p => p.IsActive == 1 && p.Year == planInfo.Year.Value);

                if (ftype == PlanFrequency.Monthly)
                    q = q.Where(p => p.MonthNum != null);
                else if (ftype == PlanFrequency.Quarterly)
                    q = q.Where(p => p.QuarterNum != null);
                else
                    q = q.Where(p => p.MonthNum != null || p.QuarterNum != null);

                list = await q
                    .OrderBy(p => p.QuarterNum ?? 0)
                    .ThenBy(p => p.MonthNum ?? 0)
                    .Select(p => new
                    {
                        p.PeriodId,
                        Label = p.MonthNum != null
                            ? $"{p.Year} — M{p.MonthNum:00} ({CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(p.MonthNum.Value)})"
                            : $"{p.Year} — Q{p.QuarterNum}"
                    })
                    .ToListAsync<object>();
            }

            ViewBag.PeriodId = new SelectList(list, "PeriodId", "Label", selected);
        }

        // ===========================
        // Frequency parsing
        // ===========================
        private enum PlanFrequency { Unknown, Monthly, Quarterly }

        private static PlanFrequency NormalizeFrequency(string? freq)
        {
            if (string.IsNullOrWhiteSpace(freq)) return PlanFrequency.Unknown;

            var f = freq.Trim().ToLowerInvariant();
            if (f.Contains("month"))   return PlanFrequency.Monthly;    // "Monthly", "month", etc.
            if (f.Contains("quarter")) return PlanFrequency.Quarterly;  // "Quarterly", "quarter"
            return PlanFrequency.Unknown;
        }
    }
}