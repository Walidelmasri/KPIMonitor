using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using System.Threading;


namespace KPIMonitor.Controllers
{
    public class KpiYearPlansController : Controller
    {
        private readonly AppDbContext _db;
        public KpiYearPlansController(AppDbContext db) => _db = db;

        // // GET: /KpiYearPlans
        // public async Task<IActionResult> Index(int? year)
        // {
        //     var q = _db.KpiYearPlans
        //                .Include(p => p.Kpi)
        //                .Include(p => p.Period)
        //                .AsNoTracking();

        //     if (year.HasValue)
        //         q = q.Where(x => x.Period != null && x.Period.Year == year.Value);

        //     var data = await q.OrderBy(x => x.Period!.Year)
        //                       .ThenBy(x => x.KpiId)
        //                       .ToListAsync();

        //     ViewBag.FilterYear = year;
        //     return View(data);
        // }
        // GET: /KpiYearPlans
        public async Task<IActionResult> Index(int? year, decimal? pillarId)
        {
            var q = _db.KpiYearPlans
                       .Include(p => p.Period)
                       .Include(p => p.Kpi)
                        .ThenInclude(k => k.Pillar)
                       .Include(p => p.Kpi)
                        .ThenInclude(k => k.Objective)
                       .AsNoTracking();

            if (year.HasValue)
                q = q.Where(x => x.Period != null && x.Period.Year == year.Value);

            if (pillarId.HasValue)
                q = q.Where(x => x.Kpi != null && x.Kpi.PillarId == pillarId.Value);

            var data = await q
            .OrderBy(x => x.Period!.Year)
            .ThenBy(x => x.Kpi!.Pillar!.PillarCode)
            .ThenBy(x => x.Kpi!.Objective!.ObjectiveCode)
            .ThenBy(x => x.Kpi!.KpiCode)
            .ToListAsync();

            // for the filters
            ViewBag.FilterYear = year;
            ViewBag.CurrentPillarId = pillarId;

            // dropdown: pillars ordered by code
            ViewBag.Pillars = new SelectList(
                await _db.DimPillars
                    .AsNoTracking()
                    .OrderBy(p => p.PillarCode)
                    .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
                    .ToListAsync(),
                "PillarId", "Name", pillarId
            );

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
        // GET: full edit modal (frequency, unit, annual target, priority, owner/editor)
        [HttpGet]
        public async Task<IActionResult> EditModal(decimal id, CancellationToken ct)
        {
            // Load plan
            var plan = await _db.KpiYearPlans
                                .AsNoTracking()
                                .FirstOrDefaultAsync(p => p.KpiYearPlanId == id, ct);
            if (plan == null) return NotFound();

            // Load employees
            var dir = HttpContext.RequestServices.GetRequiredService<IEmployeeDirectory>();
            var emps = await dir.GetAllForPickAsync(ct);

            // Reuse OwnerEditorEditVm for dropdowns + context
            var vm = new KPIMonitor.ViewModels.OwnerEditorEditVm
            {
                PlanId = plan.KpiYearPlanId,
                OwnerEmpId = plan.OwnerEmpId,
                EditorEmpId = plan.EditorEmpId,
                CurrentOwnerName = plan.Owner,
                CurrentEditorName = plan.Editor,
                Employees = emps
            };

            // Pre-fill basic fields via ViewBag (simple)
            ViewBag.Frequency = plan.Frequency ?? "";
            ViewBag.Unit = plan.Unit ?? "";
            ViewBag.AnnualTargetText = plan.AnnualTarget?.ToString("0.###") ?? "";
            ViewBag.PriorityText = plan.Priority?.ToString() ?? "";

            return PartialView("_KpiYearPlanEditModal", vm);
        }


        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePlan(
            [FromForm] decimal planId,
            [FromForm] string? frequency,
            [FromForm] string? unit,
            [FromForm] decimal? annualTarget,
            [FromForm] int? priority,
            [FromForm] string? ownerEmpId,
            [FromForm] string? editorEmpId,
            CancellationToken ct)
        {
            // ad-hoc logger without changing constructor
            var log = HttpContext.RequestServices.GetRequiredService<ILogger<KpiYearPlansController>>();

            log.LogInformation("UpdatePlan START planId={PlanId} freq={Freq} unit={Unit} target={Target} prio={Prio} ownerEmpId={OwnerEmpId} editorEmpId={EditorEmpId}",
                planId, frequency, unit, annualTarget, priority, ownerEmpId, editorEmpId);

            var plan = await _db.KpiYearPlans.FirstOrDefaultAsync(p => p.KpiYearPlanId == planId, ct);
            if (plan == null)
            {
                log.LogWarning("UpdatePlan NOT FOUND planId={PlanId}", planId);
                return NotFound("Plan not found.");
            }

            // Basic fields (even if you hid freq/unit in the table, we persist them safely)
            plan.Frequency = string.IsNullOrWhiteSpace(frequency) ? null : frequency.Trim();
            plan.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
            plan.AnnualTarget = annualTarget;
            plan.Priority = priority;

            // Directory lookups for owners/editors
            var dir = HttpContext.RequestServices.GetRequiredService<IEmployeeDirectory>();

            if (!string.IsNullOrWhiteSpace(ownerEmpId))
            {
                var owner = await dir.TryGetByEmpIdAsync(ownerEmpId!, ct);
                if (owner == null)
                {
                    log.LogWarning("UpdatePlan invalid ownerEmpId={OwnerEmpId}", ownerEmpId);
                    return BadRequest("Invalid Owner.");
                }
                plan.OwnerEmpId = owner.Value.EmpId;
                plan.Owner = owner.Value.NameEng; // ensure NAME_ENG
                log.LogInformation("UpdatePlan OWNER set to {Name} ({EmpId})", plan.Owner, plan.OwnerEmpId);
            }
            else
            {
                plan.OwnerEmpId = null;
                // keep legacy plan.Owner text (or clear if you prefer)
                log.LogInformation("UpdatePlan OWNER cleared (EmpId null)");
            }

            if (!string.IsNullOrWhiteSpace(editorEmpId))
            {
                var editor = await dir.TryGetByEmpIdAsync(editorEmpId!, ct);
                if (editor == null)
                {
                    log.LogWarning("UpdatePlan invalid editorEmpId={EditorEmpId}", editorEmpId);
                    return BadRequest("Invalid Editor.");
                }
                plan.EditorEmpId = editor.Value.EmpId;
                plan.Editor = editor.Value.NameEng; // ensure NAME_ENG
                log.LogInformation("UpdatePlan EDITOR set to {Name} ({EmpId})", plan.Editor, plan.EditorEmpId);
            }
            else
            {
                plan.EditorEmpId = null;
                log.LogInformation("UpdatePlan EDITOR cleared (EmpId null)");
            }

            await _db.SaveChangesAsync(ct);
            log.LogInformation("UpdatePlan SAVED planId={PlanId}", plan.KpiYearPlanId);

            // send back what the table expects (strings preformatted)
            return Json(new
            {
                ok = true,
                planId = plan.KpiYearPlanId,
                frequency = plan.Frequency ?? "",
                unit = plan.Unit ?? "",
                annualTargetText = plan.AnnualTarget?.ToString("0.###") ?? "—",
                priorityText = plan.Priority?.ToString() ?? "—",
                owner = plan.Owner ?? "—",   // NAME_ENG
                editor = plan.Editor ?? "—"  // NAME_ENG
            });
        }


    }
}