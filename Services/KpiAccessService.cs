using KPIMonitor.Data;                          // AppDbContext
using Microsoft.EntityFrameworkCore;           // EF Core
using Microsoft.Extensions.Configuration;      // IConfiguration
using Microsoft.Extensions.Logging;            // ILogger<T>
using System.Linq;                             
using System.Security.Claims;                  // ClaimsPrincipal
using System.Threading;                        
using System.Threading.Tasks;                  

namespace KPIMonitor.Services
{
    public sealed class KpiAccessService : IKpiAccessService
    {
        private readonly AppDbContext _db;
        private readonly IEmployeeDirectory _dir;
        private readonly IConfiguration _cfg;
        private readonly ILogger<KpiAccessService> _log;

        public KpiAccessService(AppDbContext db, IEmployeeDirectory dir, IConfiguration cfg, ILogger<KpiAccessService> log)
        {
            _db = db;
            _dir = dir;
            _cfg = cfg;
            _log = log;
        }

        public async Task<bool> CanEditPlanAsync(decimal planId, ClaimsPrincipal user, CancellationToken ct = default)
        {
            if (IsAdmin(user)) return true;

            var userId = GetSam(user); // e.g. "walid.salem"
            if (string.IsNullOrWhiteSpace(userId)) return false;

            var emp = await _dir.TryGetByUserIdAsync(userId, ct);
            if (emp == null) return false;

            var empId = emp.Value.EmpId; // "01234"

            var plan = await _db.KpiYearPlans
                .AsNoTracking()
                .Where(p => p.KpiYearPlanId == planId)
                .Select(p => new { p.OwnerEmpId, p.EditorEmpId })
                .FirstOrDefaultAsync(ct);

            if (plan == null) return false;

            var ok = string.Equals(plan.OwnerEmpId, empId) || string.Equals(plan.EditorEmpId, empId);
            _log.LogDebug("ACL planId={Plan} user={User} empId={Emp} => {Ok}", planId, userId, empId, ok);
            return ok;
        }

        public async Task<bool> CanEditKpiAsync(decimal kpiId, ClaimsPrincipal user, CancellationToken ct = default)
        {
            if (IsAdmin(user)) return true;

            var userId = GetSam(user);
            if (string.IsNullOrWhiteSpace(userId)) return false;

            var emp = await _dir.TryGetByUserIdAsync(userId, ct);
            if (emp == null) return false;

            var empId = emp.Value.EmpId;

            // owner/editor on any plan of this KPI
            var ok = await _db.KpiYearPlans
                .AsNoTracking()
                .AnyAsync(p => p.KpiId == kpiId && (p.OwnerEmpId == empId || p.EditorEmpId == empId), ct);

            _log.LogDebug("ACL kpiId={Kpi} user={User} empId={Emp} => {Ok}", kpiId, userId, empId, ok);
            return ok;
        }

        private bool IsAdmin(ClaimsPrincipal user)
        {
            var sam = GetSam(user);

            // read both AdminUsers and SuperAdminUsers from config (additive, no logic removed)
            var admins = _cfg.GetSection("App:AdminUsers").Get<string[]>() ?? new string[0];
            var superAdmins = _cfg.GetSection("App:SuperAdminUsers").Get<string[]>() ?? new string[0];

            var ok =
                admins.Any(a => string.Equals(a?.Trim(), sam, System.StringComparison.OrdinalIgnoreCase)) ||
                superAdmins.Any(a => string.Equals(a?.Trim(), sam, System.StringComparison.OrdinalIgnoreCase));

            if (ok) _log.LogDebug("ACL admin override for {User}", sam);
            return ok;
        }

        private static string GetSam(ClaimsPrincipal user)
        {
            // ClaimTypes.Name like "BADEA\\walid.salem" or "walid.salem@badea.local"
            var raw = user?.Identity?.Name ?? "";

            var idx = raw.LastIndexOf('\\');
            var name = idx >= 0 ? raw[(idx + 1)..] : raw;   // strip DOMAIN\

            var at = name.IndexOf('@');
            if (at > 0) name = name[..at];                  // strip @domain

            return name;
        }
    }
}
