using KPIMonitor.Data;
using KPIMonitor.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace KPIMonitor.Services
{
    public interface IKpiYearPlanOwnerEditorService
    {
        Task<OwnerEditorEditVm?> LoadAsync(decimal planId, CancellationToken ct = default);
        Task<bool> SaveAsync(decimal planId, string? ownerEmpId, string? editorEmpId, string changedBy, CancellationToken ct = default);
    }

    public class KpiYearPlanOwnerEditorService : IKpiYearPlanOwnerEditorService
    {
        private readonly AppDbContext _db;
        private readonly IEmployeeDirectory _dir;

        public KpiYearPlanOwnerEditorService(AppDbContext db, IEmployeeDirectory dir)
        {
            _db = db; _dir = dir;
        }

        public async Task<OwnerEditorEditVm?> LoadAsync(decimal planId, CancellationToken ct = default)
        {
            var plan = await _db.KpiYearPlans
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.KpiYearPlanId == planId, ct);
            if (plan == null) return null;

            var emps = await _dir.GetAllForPickAsync(ct);

            return new OwnerEditorEditVm
            {
                PlanId = plan.KpiYearPlanId,
                OwnerEmpId = plan.OwnerEmpId,
                EditorEmpId = plan.EditorEmpId,
                CurrentOwnerName  = plan.Owner,
                CurrentEditorName = plan.Editor,
                Employees = emps
            };
        }

        public async Task<bool> SaveAsync(decimal planId, string? ownerEmpId, string? editorEmpId, string changedBy, CancellationToken ct = default)
        {
            var plan = await _db.KpiYearPlans.FirstOrDefaultAsync(p => p.KpiYearPlanId == planId, ct);
            if (plan == null) return false;

            // Owner
            if (!string.IsNullOrWhiteSpace(ownerEmpId))
            {
                var e = await _dir.TryGetByEmpIdAsync(ownerEmpId!, ct);
                if (e == null) throw new InvalidOperationException("Owner EMP_ID not found.");
                plan.OwnerEmpId = e.Value.EmpId;
                plan.Owner      = e.Value.NameEng;   // mirror readable name
            }

            // Editor
            if (!string.IsNullOrWhiteSpace(editorEmpId))
            {
                var e = await _dir.TryGetByEmpIdAsync(editorEmpId!, ct);
                if (e == null) throw new InvalidOperationException("Editor EMP_ID not found.");
                plan.EditorEmpId = e.Value.EmpId;
                plan.Editor      = e.Value.NameEng;  // mirror readable name
            }

            // Audit if you have these columns
            plan.LastChangedBy   = changedBy;
            // plan.LastChangedDate = DateTime.UtcNow;

            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
