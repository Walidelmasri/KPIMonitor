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
using KPIMonitor.Models.ViewModels;

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

[HttpGet]
public async Task<IActionResult> Generate(decimal? kpiId, decimal? planId)
{
    var vm = new GenerateFactsVm
    {
        KpiId = kpiId,
        PlanId = planId,
        CreatedBy = User?.Identity?.Name ?? "system",
        LastChangedBy = User?.Identity?.Name ?? "system"
    };

    // Kpis dropdown (active only, sorted by code)
    var kpis = await _db.DimKpis
        .AsNoTracking()
        .Where(k => k.IsActive == 1)
        .OrderBy(k => k.KpiCode)
        .Select(k => new { k.KpiId, Label = (k.KpiCode ?? "") + " — " + (k.KpiName ?? "") })
        .ToListAsync();
    vm.Kpis = new SelectList(kpis, "KpiId", "Label", vm.KpiId);

    // Plans dropdown (when a KPI is chosen)
    if (vm.KpiId.HasValue)
    {
        var plans = await _db.KpiYearPlans
            .Include(p => p.Period)
            .AsNoTracking()
            .Where(p => p.IsActive == 1
                     && p.KpiId == vm.KpiId.Value
                     && p.Period != null
                     && p.Period.MonthNum == null
                     && p.Period.QuarterNum == null)
            .OrderBy(p => p.Period!.Year)
            .Select(p => new {
                p.KpiYearPlanId,
                Label = "Year " + p.Period!.Year,
                p.Frequency,
                Year = p.Period!.Year
            })
            .ToListAsync();

        vm.Plans = new SelectList(plans, "KpiYearPlanId", "Label", vm.PlanId);

        // If a plan is chosen, build preview
        if (vm.PlanId.HasValue)
        {
            var plan = plans.FirstOrDefault(x => x.KpiYearPlanId == vm.PlanId.Value);
            if (plan != null)
            {
                vm.PlanYear = plan.Year;
                vm.PlanFrequency = plan.Frequency;

                // Resolve frequency: plan’s own, or user-choice if blank
                var resolved = NormalizeFrequency(plan.Frequency);
                if (resolved == PlanFrequency.Unknown && !string.IsNullOrWhiteSpace(vm.FrequencyChoice))
                    resolved = NormalizeFrequency(vm.FrequencyChoice);

                // If still unknown, we just don’t preview (let user choose)
                if (resolved != PlanFrequency.Unknown && vm.PlanYear.HasValue)
                {
                    var periodsQ = _db.DimPeriods
                        .AsNoTracking()
                        .Where(p => p.IsActive == 1 && p.Year == vm.PlanYear.Value);

                    if (resolved == PlanFrequency.Monthly)
                        periodsQ = periodsQ.Where(p => p.MonthNum != null);
                    else if (resolved == PlanFrequency.Quarterly)
                        periodsQ = periodsQ.Where(p => p.QuarterNum != null);

                    var periods = await periodsQ
                        .OrderBy(p => p.QuarterNum ?? 0)
                        .ThenBy(p => p.MonthNum ?? 0)
                        .Select(p => new {
                            p.PeriodId,
                            Label = p.MonthNum != null
                                ? $"{p.Year} — M{p.MonthNum:00} ({CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(p.MonthNum.Value)})"
                                : $"{p.Year} — Q{p.QuarterNum}"
                        })
                        .ToListAsync();

                    // Which facts already exist for this plan?
                    var existing = await _db.KpiFacts
                        .AsNoTracking()
                        .Where(f => f.KpiYearPlanId == vm.PlanId.Value)
                        .Select(f => f.PeriodId)
                        .ToListAsync();
                    var existSet = existing.ToHashSet();

                    foreach (var p in periods)
                    {
                        vm.Preview.Add(new GenerateFactsVm.PeriodPreview
                        {
                            PeriodId = p.PeriodId,
                            Label = p.Label,
                            Exists = existSet.Contains(p.PeriodId)
                        });
                    }
                }
            }
        }
    }

    return View(vm);
}
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> Generate(GenerateFactsVm vm)
{
    if (!vm.KpiId.HasValue || !vm.PlanId.HasValue)
    {
        TempData["Msg"] = "Select a KPI and a Year Plan first.";
        return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
    }

    // Load plan+year and verify kpi
    var plan = await _db.KpiYearPlans
        .Include(p => p.Period)
        .AsNoTracking()
        .FirstOrDefaultAsync(p => p.KpiYearPlanId == vm.PlanId.Value);

    if (plan == null || plan.Period == null)
    {
        TempData["Msg"] = "Year plan not found.";
        return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
    }
    if (plan.KpiId != vm.KpiId.Value)
    {
        TempData["Msg"] = "Selected KPI does not match the plan.";
        return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
    }

    var resolved = NormalizeFrequency(plan.Frequency);
    if (resolved == PlanFrequency.Unknown && !string.IsNullOrWhiteSpace(vm.FrequencyChoice))
        resolved = NormalizeFrequency(vm.FrequencyChoice);

    if (resolved == PlanFrequency.Unknown)
    {
        TempData["Msg"] = "Choose a frequency (Monthly or Quarterly).";
        return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
    }

    // Pull periods for the plan’s year
    var periodsQ = _db.DimPeriods
        .AsNoTracking()
        .Where(p => p.IsActive == 1 && p.Year == plan.Period.Year);

    if (resolved == PlanFrequency.Monthly)
        periodsQ = periodsQ.Where(p => p.MonthNum != null);
    else if (resolved == PlanFrequency.Quarterly)
        periodsQ = periodsQ.Where(p => p.QuarterNum != null);

    var periods = await periodsQ
        .OrderBy(p => p.QuarterNum ?? 0)
        .ThenBy(p => p.MonthNum ?? 0)
        .Select(p => p.PeriodId)
        .ToListAsync();

    if (periods.Count == 0)
    {
        TempData["Msg"] = "No matching periods exist for that year. Create them first.";
        return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
    }

    // Existing facts for the plan
    var existingFacts = await _db.KpiFacts
        .Where(f => f.KpiYearPlanId == vm.PlanId.Value)
        .ToListAsync();
    var existMap = existingFacts.ToDictionary(f => f.PeriodId, f => f);

    var nowUser = vm.LastChangedBy ?? (User?.Identity?.Name ?? "system");
    var createdBy = vm.CreatedBy ?? nowUser;

    int toCreate = 0, toUpdate = 0;
    using var tx = await _db.Database.BeginTransactionAsync();

    try
    {
        foreach (var pid in periods)
        {
            if (existMap.TryGetValue(pid, out var existing))
            {
                // Already exists
                if (vm.OverwriteExisting && !vm.CreateMissingOnly)
                {
                    existing.LastChangedBy = nowUser;
                    // (optional: wipe values or leave them; here we just touch audit)
                    // existing.ActualValue = null; etc.
                    toUpdate++;
                }
                // else skip
            }
            else
            {
                // Missing → create
                var fact = new KpiFact
                {
                    KpiId = vm.KpiId.Value,
                    KpiYearPlanId = vm.PlanId.Value,
                    PeriodId = pid,
                    ActualValue = null,
                    TargetValue = null,
                    ForecastValue = null,
                    Budget = null,
                    StatusCode = null,
                    CreatedBy = createdBy,
                    CreatedDate = DateTime.UtcNow,
                    LastChangedBy = nowUser,
                    IsActive = 1
                };
                _db.KpiFacts.Add(fact);
                toCreate++;
            }
        }

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        TempData["Msg"] = $"Bulk generate finished: created {toCreate} fact(s){(toUpdate > 0 ? $", updated {toUpdate}" : "")}.";
    }
    catch (Exception ex)
    {
        await tx.RollbackAsync();
        TempData["Msg"] = "Failed to generate facts: " + ex.Message;
    }

    return RedirectToAction(nameof(Generate), new { kpiId = vm.KpiId, planId = vm.PlanId });
}
    }
}