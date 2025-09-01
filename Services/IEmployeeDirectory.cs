using KPIMonitor.ViewModels;

namespace KPIMonitor.Services
{
    public interface IEmployeeDirectory
    {
        Task<IReadOnlyList<EmployeePickDto>> GetAllForPickAsync(CancellationToken ct = default);
        Task<(string EmpId, string NameEng)?> TryGetByEmpIdAsync(string empId, CancellationToken ct = default);
        Task<(string EmpId, string NameEng)?> TryGetByUserIdAsync(string userId, CancellationToken ct = default);

    }
}
