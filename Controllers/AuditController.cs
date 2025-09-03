using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;
using KPIMonitor.Models; // add this

namespace KPIMonitor.Controllers
{
    public class AuditController : Controller
    {
        private readonly AppDbContext _db;
        public AuditController(AppDbContext db) { _db = db; }


[HttpGet]
public async Task<IActionResult> Index(int page = 1, int pageSize = 10)
{
    page = Math.Max(1, page);
    pageSize = Math.Clamp(pageSize, 10, 100);

    var baseQuery = _db.AuditLogs.AsNoTracking()
        .OrderByDescending(a => a.ChangedAtUtc)
        .ThenByDescending(a => a.AuditId);

    var list = await baseQuery
        .Skip((page - 1) * pageSize)
        .Take(pageSize + 1) // has-next pagination (fast; no COUNT)
        .Select(a => new AuditLog
        {
            AuditId = a.AuditId,
            TableName = a.TableName,
            KeyJson = a.KeyJson,
            Action = a.Action,
            ChangedBy = a.ChangedBy,
            ChangedAtUtc = a.ChangedAtUtc
            // ColumnChangesJson intentionally NOT selected
        })
        .ToListAsync();

    var hasNext = list.Count > pageSize;
    if (hasNext) list.RemoveAt(list.Count - 1);

    ViewBag.Page = page;
    ViewBag.PageSize = pageSize;
    ViewBag.HasNext = hasNext;

    return View(list);
}


        [HttpGet]
        public async Task<IActionResult> Changes(long id)
        {
            var json = await _db.AuditLogs.AsNoTracking()
                .Where(a => a.AuditId == id)
                .Select(a => a.ColumnChangesJson)   // only pull the CLOB when asked
                .FirstOrDefaultAsync();

            if (json is null) return NotFound();
            return Content(json, "application/json");
        }

    }
}