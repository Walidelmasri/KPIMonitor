using System;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Globalization;

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
        public async Task<IActionResult> Create(KpiAction vm, string[] ownerEmpIds)
        {
            if (!ModelState.IsValid) return View(vm);

            vm.CreatedBy = User?.Identity?.Name ?? "system";
            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedBy = vm.CreatedBy;
            vm.LastChangedDate = vm.CreatedDate;

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync(); // must save first to get ActionId

            // -------------------- OWNERS (multi) --------------------
            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleanedOwners.Count > 0)
            {
                foreach (var empId in cleanedOwners)
                {
                    _db.KpiActionOwners.Add(new KPIMonitor.Models.KpiActionOwner
                    {
                        ActionId = vm.ActionId,
                        OwnerEmpId = empId,
                        CreatedBy = vm.CreatedBy,
                        CreatedDate = DateTime.UtcNow
                    });
                }

                // UI normally sends "FirstName (+N)" in vm.Owner. If missing, fallback.
                if (string.IsNullOrWhiteSpace(vm.Owner))
                    vm.Owner = cleanedOwners[0] + (cleanedOwners.Count > 1 ? $" (+{cleanedOwners.Count - 1})" : "");

                // Ensure OWNER column is saved (in case it was empty)
                _db.KpiActions.Update(vm);
                await _db.SaveChangesAsync();
            }

            // if modal/AJAX, return OK (after owners + owner display are persisted)
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "Action created.";
            return RedirectToAction(nameof(Index), new { kpiId = vm.KpiId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateGeneral(KpiAction vm, string[] ownerEmpIds)
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

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync(); // must save first to get ActionId

            // -------------------- OWNERS (multi) --------------------
            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleanedOwners.Count > 0)
            {
                foreach (var empId in cleanedOwners)
                {
                    _db.KpiActionOwners.Add(new KPIMonitor.Models.KpiActionOwner
                    {
                        ActionId = vm.ActionId,
                        OwnerEmpId = empId,
                        CreatedBy = vm.CreatedBy,
                        CreatedDate = DateTime.UtcNow
                    });
                }

                // UI normally sends "FirstName (+N)" in vm.Owner. If missing, fallback.
                if (string.IsNullOrWhiteSpace(vm.Owner))
                    vm.Owner = cleanedOwners[0] + (cleanedOwners.Count > 1 ? $" (+{cleanedOwners.Count - 1})" : "");

                _db.KpiActions.Update(vm);
                await _db.SaveChangesAsync();
            }

            // AJAX-friendly
            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "General action created.";
            return RedirectToAction(nameof(Index));
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

            var ownerEmpIds = await _db.KpiActionOwners
                .AsNoTracking()
                .Where(o => o.ActionId == a.ActionId)
                .OrderBy(o => o.KpiActionOwnerId)
                .Select(o => o.OwnerEmpId)
                .ToListAsync();

            return Json(new
            {
                actionId = a.ActionId,
                owner = a.Owner ?? "",
                ownerEmpIds,
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
            DateTime? dueDate,
            string[] ownerEmpIds)
        {
            var act = await _db.KpiActions.FirstOrDefaultAsync(x => x.ActionId == actionId);
            if (act == null) return NotFound();

            // -------------------- OWNERS (multi) --------------------
            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Replace owner rows
            var existing = await _db.KpiActionOwners
                .Where(x => x.ActionId == actionId)
                .ToListAsync();

            if (existing.Count > 0)
                _db.KpiActionOwners.RemoveRange(existing);

            foreach (var empId in cleanedOwners)
            {
                _db.KpiActionOwners.Add(new KPIMonitor.Models.KpiActionOwner
                {
                    ActionId = actionId,
                    OwnerEmpId = empId,
                    CreatedBy = User?.Identity?.Name ?? "system",
                    CreatedDate = DateTime.UtcNow
                });
            }

            // IMPORTANT: Update the display string ONLY if owners exist
            if (cleanedOwners.Count > 0)
            {
                act.Owner = (owner ?? "").Trim();

                // fallback if owner was empty for any reason
                if (string.IsNullOrWhiteSpace(act.Owner))
                    act.Owner = cleanedOwners[0] + (cleanedOwners.Count > 1 ? $" (+{cleanedOwners.Count - 1})" : "");
            }

            act.Description = (description ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(statusCode))
                act.StatusCode = statusCode.Trim().ToLowerInvariant();

            act.AssignedAt = assignedAt;
            act.DueDate = dueDate;

            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

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

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK");

            TempData["Msg"] = "Action archived.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }
        // -------------------------
// Details + Comments (AJAX)
// -------------------------

[HttpGet]
public async Task<IActionResult> GetActionDetails(decimal actionId)
{
    var act = await _db.KpiActions
        .AsNoTracking()
        .FirstOrDefaultAsync(x => x.ActionId == actionId);

    if (act == null) return NotFound();

    // Owners (all)
    var owners = await _db.KpiActionOwners
        .AsNoTracking()
        .Where(x => x.ActionId == actionId)
        .OrderBy(x => x.KpiActionOwnerId)
        .ToListAsync();

    // Build "All names" string for the details modal
    // Prefer OwnerName if stored; fallback to EmpId
    var ownerNames = owners
        .Select(o => string.IsNullOrWhiteSpace(o.OwnerName) ? o.OwnerEmpId : o.OwnerName!.Trim())
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .ToList();

    var ownersText = ownerNames.Count == 0 ? "—" : string.Join(", ", ownerNames);

    // Permission: admin OR owner can comment
    var currentEmpId = (User?.Identity?.Name ?? "").Trim();
    var isOwner = !string.IsNullOrWhiteSpace(currentEmpId) &&
                  owners.Any(o => string.Equals(o.OwnerEmpId, currentEmpId, StringComparison.OrdinalIgnoreCase));

    // You already use these strings in the page; keep it simple here:
    // If you want stricter admin detection later, we can wire AdminAuthorizer in.
    var isAdmin = User?.IsInRole("Admin") == true || User?.IsInRole("SuperAdmin") == true;

    var canComment = isAdmin || isOwner;

    static string F(DateTime? dt) => dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

    string StatusText(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return c switch
        {
            "todo" => "To Do",
            "inprogress" => "In Progress",
            "done" => "Done",
            "archived" => "Archived",
            _ => string.IsNullOrWhiteSpace(code) ? "—" : code
        };
    }

    return Json(new
    {
        actionId = act.ActionId,
        description = act.Description ?? "",
        statusCode = act.StatusCode ?? "todo",
        statusText = StatusText(act.StatusCode),
        dueDateLocal = F(act.DueDate),

        ownersText,
        canComment
    });
}

[HttpGet]
public async Task<IActionResult> GetActionComments(decimal actionId)
{
    // Validate action exists (optional but keeps things clean)
    var exists = await _db.KpiActions.AsNoTracking().AnyAsync(x => x.ActionId == actionId);
    if (!exists) return NotFound();

    var comments = await _db.KpiActionComments
        .AsNoTracking()
        .Where(x => x.ActionId == actionId)
        .OrderBy(x => x.CreatedDate) // OLDEST -> NEWEST ✅
        .ThenBy(x => x.KpiActionCommentId)
        .ToListAsync();

    static string F(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    return Json(comments.Select(c => new
    {
        id = c.KpiActionCommentId,
        actionId = c.ActionId,
        text = c.CommentText ?? "",
        authorEmpId = c.CreatedByEmpId ?? "",
        authorName = c.CreatedByName ?? "",
        createdAtLocal = F(c.CreatedDate)
    }));
}

[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> AddActionComment(decimal actionId, string text)
{
    var trimmed = (text ?? "").Trim();
    if (string.IsNullOrWhiteSpace(trimmed))
        return BadRequest("Comment text is required.");

    var act = await _db.KpiActions.FirstOrDefaultAsync(x => x.ActionId == actionId);
    if (act == null) return NotFound();

    // Owners
    var owners = await _db.KpiActionOwners
        .AsNoTracking()
        .Where(x => x.ActionId == actionId)
        .Select(x => x.OwnerEmpId)
        .ToListAsync();

    var currentEmpId = (User?.Identity?.Name ?? "").Trim();

    var isOwner = !string.IsNullOrWhiteSpace(currentEmpId) &&
                  owners.Any(o => string.Equals(o, currentEmpId, StringComparison.OrdinalIgnoreCase));

    // Basic admin check (role-based).
    // If your app does NOT use roles, tell me what your admin claim looks like and I’ll swap it.
    var isAdmin = User?.IsInRole("Admin") == true || User?.IsInRole("SuperAdmin") == true;

    if (!isOwner && !isAdmin)
        return Forbid();

    var now = DateTime.UtcNow;

    var comment = new KpiActionComment
    {
        ActionId = actionId,
        CommentText = trimmed,
        CreatedByEmpId = string.IsNullOrWhiteSpace(currentEmpId) ? "system" : currentEmpId,
        CreatedByName = string.IsNullOrWhiteSpace(currentEmpId) ? "system" : currentEmpId, // replace later if you want real display names
        CreatedDate = now
    };

    _db.KpiActionComments.Add(comment);
    await _db.SaveChangesAsync();

    // AJAX-friendly
    if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        return Json(new { ok = true });

    return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
}

    }
    
}
