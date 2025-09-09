using System.Threading.Tasks;
using KPIMonitor.Models.ViewModels;

namespace KPIMonitor.Services.Abstractions
{
    public interface IPriorityMatrixService
    {
        /// <summary>
        /// Build the matrix. If year is provided, "latest status per KPI" is restricted to that year;
        /// otherwise we take the latest non-empty status across all years.
        /// </summary>
        Task<PriorityMatrixVm> BuildAsync(int? year = null);
    }
}
