using System.Threading.Tasks;
using KPIMonitor.Models.ViewModels;

namespace KPIMonitor.Services.Abstractions
{
    public interface IStrategyMapService
    {
        Task<StrategyMapVm> BuildAsync(int? year = null);
    }
}
