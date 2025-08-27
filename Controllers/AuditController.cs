using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using KPIMonitor.Data;

namespace KPIMonitor.Controllers
{
    public class AuditController : Controller
    {
        private readonly AppDbContext _db;
        public AuditController(AppDbContext db) { _db = db; }

        [HttpGet]
        public async Task<IActionResult> Index(int take = 500)
        {
            var rows = await _db.AuditLogs
                .AsNoTracking()
                .OrderByDescending(a => a.ChangedAtUtc)
                .Take(Math.Clamp(take, 50, 5000))
                .ToListAsync();

            return View(rows);
        }
    }
}