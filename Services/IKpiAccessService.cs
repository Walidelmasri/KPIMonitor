namespace KPIMonitor.Services
{
    using System.Security.Claims;
    using System.Threading;
    using System.Threading.Tasks;

    public interface IKpiAccessService
    {
        Task<bool> CanEditPlanAsync(decimal planId, ClaimsPrincipal user, CancellationToken ct = default);
        Task<bool> CanEditKpiAsync(decimal kpiId, ClaimsPrincipal user, CancellationToken ct = default);
    }
}
