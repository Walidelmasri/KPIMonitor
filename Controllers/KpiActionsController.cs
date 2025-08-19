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

        // Create (POST)
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

            TempData["Msg"] = "Action created.";
            return RedirectToAction(nameof(Index), new { kpiId = vm.KpiId });
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

        // Move deadline (enforce <= 3 extensions, record history)
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

            TempData["Msg"] = "Deadline moved.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }

        // Update status only (todo/inprogress/done)
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

            TempData["Msg"] = "Status updated.";
            return RedirectToAction(nameof(Index), new { kpiId = act.KpiId });
        }
    }
}