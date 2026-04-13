using KPIMonitor.ViewModels;

namespace KPIMonitor.Services
{
    public interface IEmployeeDirectory
    {
        Task<IReadOnlyList<EmployeePickDto>> GetAllForPickAsync(CancellationToken ct = default);
        Task<(string EmpId, string NameEng, string? NameAr)?> TryGetByEmpIdAsync(string empId, CancellationToken ct = default);
        Task<(string EmpId, string NameEng, string? NameAr)?> TryGetByUserIdAsync(string userId, CancellationToken ct = default);

        // Used to resolve email from an EMP_ID when OwnerLogin is empty
        Task<string?> TryGetLoginByEmpIdAsync(string empId, CancellationToken ct = default);
    }
}
