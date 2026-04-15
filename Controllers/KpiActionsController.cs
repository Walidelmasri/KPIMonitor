using System;
using System.Linq;
using System.Threading.Tasks;
using KPIMonitor.Data;
using KPIMonitor.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Globalization;
using KPIMonitor.Services;

namespace KPIMonitor.Controllers
{
    public class KpiActionsController : Controller
    {
        private readonly AppDbContext _db;
        private readonly IEmployeeDirectory _empDir;
        private readonly IAdminAuthorizer _adminAuthorizer;

        public KpiActionsController(AppDbContext db, IEmployeeDirectory empDir, IAdminAuthorizer adminAuthorizer)
        {
            _db = db;
            _empDir = empDir;
            _adminAuthorizer = adminAuthorizer;
        }

        private bool IsArabicUi()
        {
            return CultureInfo.CurrentUICulture.Name.StartsWith("ar", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<string?> ResolveEmployeeNameAsync(string? empId)
        {
            var id = (empId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id))
                return null;

            var pick = await _empDir.TryGetByEmpIdAsync(id);
            if (!pick.HasValue)
                return null;

            var value = pick.Value;

            var name = IsArabicUi()
                ? (!string.IsNullOrWhiteSpace(value.NameAr) ? value.NameAr! : (value.NameEng ?? ""))
                : (!string.IsNullOrWhiteSpace(value.NameEng) ? value.NameEng : (value.NameAr ?? ""));

            name = (name ?? "").Trim();

            var idx = name.IndexOf('(');
            if (idx > 0)
                name = name.Substring(0, idx).Trim();

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        private string LocalizedStatusText(string? code)
        {
            var c = (code ?? "").Trim().ToLowerInvariant();
            var isArabic = IsArabicUi();

            return c switch
            {
                "todo" => isArabic ? "المطلوب تنفيذها" : "To Do",
                "inprogress" => isArabic ? "قيد التنفيذ" : "In Progress",
                "done" => isArabic ? "المنجزة" : "Done",
                "archived" => isArabic ? "مؤرشف" : "Archived",
                _ => string.IsNullOrWhiteSpace(code) ? "—" : code
            };
        }

        public async Task<IActionResult> Index(decimal? kpiId)
        {
            var q = _db.KpiActions
                .Include(a => a.Kpi)
                .AsNoTracking();

            if (kpiId.HasValue)
                q = q.Where(a => a.KpiId == kpiId.Value);

            var data = await q
                .OrderBy(a => a.StatusCode)
                .ThenBy(a => a.DueDate)
                .ToListAsync();

            ViewBag.KpiId = kpiId;
            return View(data);
        }

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
            await _db.SaveChangesAsync();

            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var empId in cleanedOwners)
            {
                var ownerName = await ResolveEmployeeNameAsync(empId);

                _db.KpiActionOwners.Add(new KpiActionOwner
                {
                    ActionId = vm.ActionId,
                    OwnerEmpId = empId,
                    OwnerName = string.IsNullOrWhiteSpace(ownerName) ? null : ownerName,
                    CreatedBy = vm.CreatedBy,
                    CreatedDate = DateTime.UtcNow
                });
            }

            if (cleanedOwners.Count > 0)
            {
                var firstName = _db.KpiActionOwners.Local.FirstOrDefault(x => x.ActionId == vm.ActionId)?.OwnerName;

                vm.Owner = cleanedOwners.Count > 1
                    ? $"{(string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName)} (+{cleanedOwners.Count - 1})"
                    : (string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName);

                await _db.SaveChangesAsync();
            }

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

            vm.KpiId = null;
            vm.IsGeneral = true;

            vm.CreatedBy = User?.Identity?.Name ?? "system";
            vm.CreatedDate = DateTime.UtcNow;
            vm.LastChangedBy = vm.CreatedBy;
            vm.LastChangedDate = vm.CreatedDate;

            if (vm.AssignedAt == null) vm.AssignedAt = DateTime.UtcNow;
            vm.StatusCode = string.IsNullOrWhiteSpace(vm.StatusCode) ? "todo" : vm.StatusCode.Trim().ToLowerInvariant();

            _db.KpiActions.Add(vm);
            await _db.SaveChangesAsync();

            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var empId in cleanedOwners)
            {
                var ownerName = await ResolveEmployeeNameAsync(empId);

                _db.KpiActionOwners.Add(new KpiActionOwner
                {
                    ActionId = vm.ActionId,
                    OwnerEmpId = empId,
                    OwnerName = string.IsNullOrWhiteSpace(ownerName) ? null : ownerName,
                    CreatedBy = vm.CreatedBy,
                    CreatedDate = DateTime.UtcNow
                });
            }

            if (cleanedOwners.Count > 0)
            {
                var firstName = _db.KpiActionOwners.Local.FirstOrDefault(x => x.ActionId == vm.ActionId)?.OwnerName;

                vm.Owner = cleanedOwners.Count > 1
                    ? $"{(string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName)} (+{cleanedOwners.Count - 1})"
                    : (string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName);

                await _db.SaveChangesAsync();
            }

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK", "text/html");

            TempData["Msg"] = "General action created.";
            return RedirectToAction(nameof(Index));
        }

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

            var cleanedOwners = (ownerEmpIds ?? Array.Empty<string>())
                .Select(x => (x ?? "").Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var existing = await _db.KpiActionOwners
                .Where(x => x.ActionId == actionId)
                .ToListAsync();

            if (existing.Count > 0)
                _db.KpiActionOwners.RemoveRange(existing);

            foreach (var empId in cleanedOwners)
            {
                var ownerName = await ResolveEmployeeNameAsync(empId);

                _db.KpiActionOwners.Add(new KpiActionOwner
                {
                    ActionId = actionId,
                    OwnerEmpId = empId,
                    OwnerName = string.IsNullOrWhiteSpace(ownerName) ? null : ownerName,
                    CreatedBy = User?.Identity?.Name ?? "system",
                    CreatedDate = DateTime.UtcNow
                });
            }

            if (cleanedOwners.Count > 0)
            {
                var firstName = _db.KpiActionOwners.Local
                    .FirstOrDefault(x => x.ActionId == actionId)?.OwnerName;

                act.Owner = cleanedOwners.Count > 1
                    ? $"{(string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName)} (+{cleanedOwners.Count - 1})"
                    : (string.IsNullOrWhiteSpace(firstName) ? cleanedOwners[0] : firstName);
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

            act.StatusCode = "archived";
            act.LastChangedBy = User?.Identity?.Name ?? "system";
            act.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Content("OK");

            TempData["Msg"] = "Action archived.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }

        [HttpGet]
        public async Task<IActionResult> GetActionDetails(decimal actionId)
        {
            var act = await _db.KpiActions
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ActionId == actionId);

            if (act == null) return NotFound();

            var owners = await _db.KpiActionOwners
                .AsNoTracking()
                .Where(x => x.ActionId == actionId)
                .OrderBy(x => x.KpiActionOwnerId)
                .ToListAsync();

            var ownerNames = new List<string>();

            foreach (var o in owners)
            {
                var name = (o.OwnerName ?? "").Trim();

                if (string.IsNullOrWhiteSpace(name))
                    name = (await ResolveEmployeeNameAsync(o.OwnerEmpId)) ?? "";

                ownerNames.Add(string.IsNullOrWhiteSpace(name) ? o.OwnerEmpId : name);
            }

            var ownersText = ownerNames.Count == 0 ? "—" : string.Join(", ", ownerNames);

            var currentEmpId = (User?.Identity?.Name ?? "").Trim();
            var isOwner = !string.IsNullOrWhiteSpace(currentEmpId) &&
                          owners.Any(o => string.Equals(o.OwnerEmpId, currentEmpId, StringComparison.OrdinalIgnoreCase));

            var isAdmin = _adminAuthorizer.IsAdmin(User) || _adminAuthorizer.IsSuperAdmin(User);
            var canComment = isAdmin || isOwner;

            static string F(DateTime? dt) => dt.HasValue ? dt.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "—";

            return Json(new
            {
                actionId = act.ActionId,
                description = act.Description ?? "",
                statusCode = act.StatusCode ?? "todo",
                statusText = LocalizedStatusText(act.StatusCode),
                dueDateLocal = F(act.DueDate),
                ownersText,
                canComment
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetActionComments(decimal actionId)
        {
            var exists = await _db.KpiActions.AsNoTracking().AnyAsync(x => x.ActionId == actionId);
            if (!exists) return NotFound();

            var comments = await _db.KpiActionComments
                .AsNoTracking()
                .Where(x => x.ActionId == actionId)
                .OrderBy(x => x.CreatedDate)
                .ThenBy(x => x.KpiActionCommentId)
                .ToListAsync();

            static string F(DateTime dt) => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

            var result = new List<object>();

            foreach (var c in comments)
            {
                var resolvedName = await ResolveEmployeeNameAsync(c.CreatedByEmpId);
                var authorName = !string.IsNullOrWhiteSpace(resolvedName)
                    ? resolvedName
                    : (string.IsNullOrWhiteSpace(c.CreatedByName) ? (c.CreatedByEmpId ?? "") : c.CreatedByName);

                result.Add(new
                {
                    id = c.KpiActionCommentId,
                    actionId = c.ActionId,
                    text = c.CommentText ?? "",
                    authorEmpId = c.CreatedByEmpId ?? "",
                    authorName,
                    createdAtLocal = F(c.CreatedDate)
                });
            }

            return Json(result);
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

            var owners = await _db.KpiActionOwners
                .AsNoTracking()
                .Where(x => x.ActionId == actionId)
                .Select(x => x.OwnerEmpId)
                .ToListAsync();

            var currentEmpId = (User?.Identity?.Name ?? "").Trim();

            var isOwner = !string.IsNullOrWhiteSpace(currentEmpId) &&
                          owners.Any(o => string.Equals(o, currentEmpId, StringComparison.OrdinalIgnoreCase));

            var isAdmin = _adminAuthorizer.IsAdmin(User) || _adminAuthorizer.IsSuperAdmin(User);

            if (!isOwner && !isAdmin)
                return Forbid();

            var now = DateTime.UtcNow;
            var authorEmpId = string.IsNullOrWhiteSpace(currentEmpId) ? "system" : currentEmpId;

            string? authorName = null;
            if (!string.Equals(authorEmpId, "system", StringComparison.OrdinalIgnoreCase))
                authorName = await ResolveEmployeeNameAsync(authorEmpId);

            var comment = new KpiActionComment
            {
                ActionId = actionId,
                CommentText = trimmed,
                CreatedByEmpId = authorEmpId,
                CreatedByName = authorName ?? authorEmpId,
                CreatedDate = now
            };

            _db.KpiActionComments.Add(comment);
            await _db.SaveChangesAsync();

            if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
                return Json(new { ok = true });

            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }
    }
}