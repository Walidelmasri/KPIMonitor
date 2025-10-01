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
            return View(new KpiYearPlan { IsActive = 1, CreatedBy = user, LastChangedBy = user, TargetDirection = 1 });
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

            // Require direction explicitly (1 or -1)
            if (vm.TargetDirection != 1 && vm.TargetDirection != -1)
                ModelState.AddModelError(nameof(vm.TargetDirection), "Select whether higher or lower is better.");

            if (!ModelState.IsValid)
            {
                await LoadKpisAsync(vm.KpiId);
                await LoadYearPeriodsAsync((int?)vm.PeriodId);
                return View(vm);
            }

            vm.CreatedDate = DateTime.UtcNow;
            _db.KpiYearPlans.Add(vm); // <-- FIX: proper Add

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

        // GET: full edit modal (frequency, unit, annual target, priority, owner/editor, direction)
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

            // Direction (1 or -1)
            ViewBag.TargetDirection = plan.TargetDirection;

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

            // Basic fields
            plan.Frequency = string.IsNullOrWhiteSpace(frequency) ? null : frequency.Trim();
            plan.Unit = string.IsNullOrWhiteSpace(unit) ? null : unit.Trim();
            plan.AnnualTarget = annualTarget;
            plan.Priority = priority;

            // Target direction (required; must be 1 or -1)
            var dirRaw = Request.Form["targetDirection"].ToString();
            if (!int.TryParse(dirRaw, out var targetDir) || (targetDir != 1 && targetDir != -1))
                return BadRequest("Invalid Target Direction. Choose 1 or -1.");
            plan.TargetDirection = targetDir;

            // Directory lookups for owners/editors
            var directory = HttpContext.RequestServices.GetRequiredService<IEmployeeDirectory>();

            if (!string.IsNullOrWhiteSpace(ownerEmpId))
            {
                var owner = await directory.TryGetByEmpIdAsync(ownerEmpId!, ct);
                if (owner == null)
                    return BadRequest("Invalid Owner.");

                plan.Owner = owner.Value.NameEng;     // keep NAME_ENG behavior
                plan.OwnerEmpId = owner.Value.EmpId;

                var ownerLoginUp = ExtractLoginUpper(owner.Value)
                                   ?? ExtractLoginUpper(ownerEmpId)
                                   ?? (string?)null;

                plan.OwnerLogin = string.IsNullOrWhiteSpace(ownerLoginUp)
                    ? null
                    : ownerLoginUp; // already UPPER
            }
            else
            {
                plan.OwnerEmpId = null;
                plan.OwnerLogin = null;
            }

            if (!string.IsNullOrWhiteSpace(editorEmpId))
            {
                var editor = await directory.TryGetByEmpIdAsync(editorEmpId!, ct);
                if (editor == null)
                    return BadRequest("Invalid Editor.");

                plan.Editor = editor.Value.NameEng;
                plan.EditorEmpId = editor.Value.EmpId;

                var editorLoginUp = ExtractLoginUpper(editor.Value)
                                    ?? ExtractLoginUpper(editorEmpId)
                                    ?? (string?)null;

                plan.EditorLogin = string.IsNullOrWhiteSpace(editorLoginUp)
                    ? null
                    : editorLoginUp;
            }
            else
            {
                plan.EditorEmpId = null;
                plan.EditorLogin = null;
            }

            await _db.SaveChangesAsync(ct);
            log.LogInformation("UpdatePlan SAVED planId={PlanId}", plan.KpiYearPlanId);

            // friendly label for optional display
            string directionText = plan.TargetDirection == 1
                ? "Higher is better (≥)"
                : "Lower is better (≤)";

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
                editor = plan.Editor ?? "—", // NAME_ENG
                directionText
            });
        }

        private static string Sam(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');             // DOMAIN\user
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');                  // user@domain
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        // Normalize a login: remove "DOMAIN\" and "@domain", trim.
        private static string NormalizeLogin(string? raw)
        {
            var s = raw ?? "";
            var bs = s.LastIndexOf('\\');                  // DOMAIN\user
            if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            var at = s.IndexOf('@');                       // user@domain
            if (at > 0) s = s[..at];
            return s.Trim();
        }

        // Try to extract a login (uppercased) from any directory object or string.
        private static string? ExtractLoginUpper(object? person)
        {
            if (person == null) return null;

            // (login, name) tuple support
            if (person is (string login, string _))
            {
                var norm = NormalizeLogin(login);
                return string.IsNullOrWhiteSpace(norm) ? null : norm.ToUpperInvariant();
            }

            // Reflection-based: try common property names
            var type = person.GetType();
            string? GetStr(string prop)
                => type.GetProperty(prop)?.GetValue(person) as string;

            var candidates = new[]
            {
                "Login", "Sam", "SamAccountName", "UserName", "User",
                "Upn", "UPN", "Email", "EmailAddress", "Mail"
            };

            foreach (var prop in candidates)
            {
                var val = GetStr(prop);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var norm = NormalizeLogin(val);
                    if (!string.IsNullOrWhiteSpace(norm))
                        return norm.ToUpperInvariant();
                }
            }

            return null;
        }
    }
}
