using System;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Controllers
{
    public class KpiActionsController : Controller
    {
        private readonly AppDbContext _db;
        public KpiActionsController(AppDbContext db) { _db = db; }

        // List (optionally filtered by KPI)
        public async Task<IActionResult> Index(decimal? kpiId)
        {
            var q = _db.KpiActions
                .Include(a => a.Kpi)
                .AsNoTracking();

            if (kpiId.HasValue)
                q = q.Where(a => a.KpiId == kpiId.Value);

            var data = await q
                .OrderBy(a => a.StatusCode)   // tweak as you like
                .ThenBy(a => a.DueDate)
                .ToListAsync();

            ViewBag.KpiId = kpiId;
            return View(data);
        }

        // Create (GET)
        public IActionResult Create(decimal? kpiId)
        {
            var vm = new KpiAction
            {
                KpiId = kpiId ?? 0,
                AssignedAt = DateTime.UtcNow,
                ExtensionCount = 0,
                StatusCode = "todo"
            };
            return View(vm);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(KpiAction vm)
        {
            if (!ModelState.IsValid) return View(vm);

            vm.CreatedBy = User?.Identity?.Name ?? "system";
            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedBy = vm.CreatedBy;
            vm.LastChangedDate = vm.CreatedDate;

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync();

            // NEW: if modal/AJAX, just return 200 OK (no redirect)
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "Action created.";
            return RedirectToAction(nameof(Index), new { kpiId = vm.KpiId });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateGeneral(KpiAction vm)
        {
            if (vm == null || string.IsNullOrWhiteSpace(vm.Description))
                return BadRequest("Description is required.");

            // 🔒 Server-enforce "general" (ignore anything sent from the client)
            vm.KpiId = null;
            vm.IsGeneral = true;

            // Set timestamps and audit fields like your existing Create
            vm.CreatedBy = User?.Identity?.Name ?? "system";
            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedBy = vm.CreatedBy;
            vm.LastChangedDate = vm.CreatedDate;

            // Reasonable defaults if not posted
            if (vm.AssignedAt == null) vm.AssignedAt = DateTime.UtcNow;
            vm.StatusCode = string.IsNullOrWhiteSpace(vm.StatusCode) ? "todo" : vm.StatusCode.Trim().ToLowerInvariant();
            // ExtensionCount is already short (0 by default from your GET Create path); if null/0 it's fine.

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync();

            // Match your AJAX pattern from Create: return a simple OK if this was an XHR
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "General action created.";
            return RedirectToAction(nameof(Index)); // no kpiId → shows all (or change to your preferred landing)
        }
        // Edit (basic fields)
        public async Task<IActionResult> Edit(decimal id)
        {
            var item = await _db.KpiActions.FindAsync(id);
            if (item == null) return NotFound();
            return View(item);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(KpiAction vm)
        {
            var item = await _db.KpiActions.FirstOrDefaultAsync(a => a.ActionId == vm.ActionId);
            if (item == null) return NotFound();

            // Update minimal fields
            item.Owner = vm.Owner;
            item.Description = vm.Description;
            item.DueDate = vm.DueDate;
            item.StatusCode = vm.StatusCode;
            item.LastChangedBy = User?.Identity?.Name ?? "system";
            item.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            TempData["Msg"] = "Action updated.";
            return RedirectToAction(nameof(Index), new { kpiId = item.KpiId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveDeadline(decimal actionId, DateTime newDueDate, string? reason)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
            if (act == null) return NotFound();

            if (act.ExtensionCount >= 3)
            {
                TempData["Msg"] = "Maximum of 3 deadline extensions reached.";
                return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
            }

            var hist = new KpiActionDeadlineHistory
            {
                ActionId = act.ActionId,
                OldDueDate = act.DueDate,
                NewDueDate = newDueDate,
                ChangedAt = DateTime.UtcNow,
                ChangedBy = User?.Identity?.Name ?? "system",
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
            };
            _db.KpiActionDeadlineHistories.Add(hist);

            act.DueDate = newDueDate;
            act.ExtensionCount = (short)(act.ExtensionCount + 1);
            act.LastChangedBy = hist.ChangedBy;
            act.LastChangedDate = hist.ChangedAt;

            await _db.SaveChangesAsync();

            // NEW
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "Deadline moved.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetStatus(decimal actionId, string statusCode)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(a => a.ActionId == actionId);
            if (act == null) return NotFound();

            act.StatusCode = statusCode?.Trim().ToLowerInvariant() ?? "todo";
            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // NEW
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK");

            TempData["Msg"] = "Status updated.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }
        [HttpGet]
public async Task<IActionResult> GetAction(decimal actionId)
{
    var a = await _db.KpiActions
        .AsNoTracking()
        .FirstOrDefaultAsync(x => x.ActionId == actionId);

    if (a == null) return NotFound();

    // format to feed <input type="datetime-local">
    static string AsLocal(DateTime? dt) => dt.HasValue ? dt.Value.ToString("yyyy-MM-ddTHH:mm") : "";

    return Json(new
    {
        actionId = a.ActionId,
        owner = a.Owner ?? "",
        description = a.Description ?? "",
        statusCode = a.StatusCode ?? "todo",
        assignedAtLocal = AsLocal(a.AssignedAt),
        dueDateLocal = AsLocal(a.DueDate)
    });
}

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateAction(
            decimal actionId,
            string? owner,
            string? description,
            string? statusCode,
            DateTime? assignedAt,
            DateTime? dueDate)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(x => x.ActionId == actionId);
            if (act == null) return NotFound();

            act.Owner = (owner ?? "").Trim();
            act.Description = (description ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(statusCode))
                act.StatusCode = statusCode.Trim().ToLowerInvariant();
            act.AssignedAt = assignedAt;
            act.DueDate = dueDate;

            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // AJAX-friendly
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK");

            TempData["Msg"] = "Action updated.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ArchiveAction(decimal actionId)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(x => x.ActionId == actionId);
            if (act == null) return NotFound();

            // Mark as archived
            act.StatusCode = "archived";
            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            // AJAX-friendly
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK");

            TempData["Msg"] = "Action archived.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }

    }
}