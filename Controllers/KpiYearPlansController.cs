using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models;
using KPIMonitor.Services;
using System.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KPIMonitor.Controllers
{
    public class KpiYearPlansController : Controller
    {
        private readonly AppDbContext _db;
        public KpiYearPlansController(AppDbContext db) => _db = db;

        // --------- Clone Year Plans (admin utility) ---------

        [HttpGet]
        [Microsoft.AspNetCore.Authorization.Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> CloneModal(CancellationToken ct)
        {
            // Year periods
            var years = await _db.DimPeriods
                .AsNoTracking()
                .Where(p => p.IsActive == 1 && p.MonthNum == null && p.QuarterNum == null)
                .OrderBy(p => p.Year)
                .Select(p => new { p.PeriodId, Label = "Year " + p.Year })
                .ToListAsync(ct);
            ViewBag.YearPeriods = new SelectList(years, "PeriodId", "Label");

            // Pillars
            var pillars = await _db.DimPillars
                .AsNoTracking()
                .OrderBy(p => p.PillarCode)
                .Select(p => new { p.PillarId, Name = p.PillarCode + " — " + p.PillarName })
                .ToListAsync(ct);
            ViewBag.Pillars = new SelectList(pillars, "PillarId", "Name");

            // KPIs
            var kpis = await _db.DimKpis
                .AsNoTracking()
                .Where(k => k.IsActive == 1)
                .OrderBy(k => k.KpiCode)
                .Select(k => new { k.KpiId, Label = k.KpiCode + " — " + k.KpiName })
                .ToListAsync(ct);
            ViewBag.Kpis = new SelectList(kpis, "KpiId", "Label");

            return PartialView("_KpiYearPlanCloneModal");
        }

        public sealed class CloneRequest
        {
            public string Scope { get; set; } = "kpi";          // "kpi" | "pillar"
            public decimal SourceYearPeriodId { get; set; }
            public decimal TargetYearPeriodId { get; set; }
            public decimal? KpiId { get; set; }
            public decimal? PillarId { get; set; }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Clone([FromForm] CloneRequest req, CancellationToken ct)
        {
            if (req.SourceYearPeriodId == req.TargetYearPeriodId)
                return BadRequest("Source year and target year must be different.");

            // Validate YEAR periods
            async Task<bool> IsYearPeriod(decimal pid) => await _db.DimPeriods
                .AsNoTracking()
                .AnyAsync(p => p.PeriodId == pid && p.MonthNum == null && p.QuarterNum == null, ct);

            if (!await IsYearPeriod(req.SourceYearPeriodId) || !await IsYearPeriod(req.TargetYearPeriodId))
                return BadRequest("Invalid year period selection.");

            // Determine KPI scope
            List<decimal> kpiIds;
            if (string.Equals(req.Scope, "pillar", StringComparison.OrdinalIgnoreCase))
            {
                if (req.PillarId == null) return BadRequest("Pillar is required.");

                kpiIds = await _db.DimKpis
                    .AsNoTracking()
                    .Where(k => k.IsActive == 1 && k.PillarId == req.PillarId.Value)
                    .Select(k => k.KpiId)
                    .ToListAsync(ct);

                if (kpiIds.Count == 0)
                    return Json(new { ok = true, created = 0, skipped = 0, failed = 0, details = Array.Empty<object>() });
            }
            else
            {
                if (req.KpiId == null) return BadRequest("KPI is required.");
                kpiIds = new List<decimal> { req.KpiId.Value };
            }

            // Load source & target plans for these KPIs
            var sourcePlans = await _db.KpiYearPlans
                .AsNoTracking()
                .Where(p => p.PeriodId == req.SourceYearPeriodId && kpiIds.Contains(p.KpiId))
                .ToListAsync(ct);

            var targetPlansExisting = await _db.KpiYearPlans
                .AsNoTracking()
                .Where(p => p.PeriodId == req.TargetYearPeriodId && kpiIds.Contains(p.KpiId))
                .Select(p => p.KpiId)
                .ToListAsync(ct);

            var targetSet = targetPlansExisting.ToHashSet();
            var sourceMap = sourcePlans.ToDictionary(p => p.KpiId, p => p);

            int created = 0, skipped = 0, failed = 0;
            var details = new List<object>();

            var who = User?.Identity?.Name ?? "system";
            var now = DateTime.UtcNow;

            foreach (var kpiId in kpiIds)
            {
                if (!sourceMap.TryGetValue(kpiId, out var src))
                {
                    failed++;
                    details.Add(new { kpiId, status = "failed", reason = "No source year plan" });
                    continue;
                }

                if (targetSet.Contains(kpiId))
                {
                    skipped++;
                    details.Add(new { kpiId, status = "skipped", reason = "Target year plan already exists" });
                    continue;
                }

                // Copy every plan field 1:1, only swap PeriodId + audit fields
                var clone = new KpiYearPlan
                {
                    KpiId = src.KpiId,
                    PeriodId = req.TargetYearPeriodId,

                    Frequency = src.Frequency,
                    AnnualTarget = src.AnnualTarget,
                    AnnualBudget = src.AnnualBudget,
                    Priority = src.Priority,
                    Owner = src.Owner,
                    Editor = src.Editor,
                    OwnerLogin = src.OwnerLogin,
                    EditorLogin = src.EditorLogin,
                    Unit = src.Unit,
                    TargetDirection = src.TargetDirection,
                    IsActive = src.IsActive,

                    OwnerEmpId = src.OwnerEmpId,
                    EditorEmpId = src.EditorEmpId,
                    Editor2EmpId = src.Editor2EmpId,
                    Editor2 = src.Editor2,

                    CreatedBy = who,
                    LastChangedBy = who,
                    CreatedDate = now
                };

                _db.KpiYearPlans.Add(clone);
                created++;
                details.Add(new { kpiId, status = "created" });
            }

            try
            {
                if (created > 0)
                    await _db.SaveChangesAsync(ct);

                return Json(new { ok = true, created, skipped, failed, details });
            }
            catch (DbUpdateException ex)
            {
                return StatusCode(500, "Clone failed: " + ex.Message);
            }
        }

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
                Editor2EmpId = plan.Editor2EmpId,
                CurrentEditor2Name = plan.Editor2,
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
            [FromForm] string? editor2EmpId,
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
            // Optional secondary editor (same permissions as primary editor)
            if (!string.IsNullOrWhiteSpace(editor2EmpId))
            {
                if (!string.IsNullOrWhiteSpace(editorEmpId) &&
                    string.Equals(editor2EmpId, editorEmpId, StringComparison.OrdinalIgnoreCase))
                    return BadRequest("Secondary Editor cannot be the same as the primary Editor.");

                var editor2 = await directory.TryGetByEmpIdAsync(editor2EmpId!, ct);
                if (editor2 == null)
                    return BadRequest("Invalid Secondary Editor.");

                plan.Editor2EmpId = editor2.Value.EmpId;
                plan.Editor2 = editor2.Value.NameEng;
            }
            else
            {
                plan.Editor2EmpId = null;
                plan.Editor2 = null;
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
                editor = string.IsNullOrWhiteSpace(plan.Editor2)
    ? (plan.Editor ?? "—")
    : $"{(plan.Editor ?? "—")} / {plan.Editor2}",
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
