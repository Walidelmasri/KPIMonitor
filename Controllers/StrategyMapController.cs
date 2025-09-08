using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Services.Abstractions;

namespace KPIMonitor.Controllers
{
    public class StrategyMapController : Controller
    {
        private readonly IStrategyMapService _service;
        public StrategyMapController(IStrategyMapService service) => _service = service;

        [HttpGet]
        public async Task<IActionResult> Index(int? year = null)
        {
            var vm = await _service.BuildAsync(year);
            ViewData["Title"] = "Strategy Map";
            return View(vm);
        }
    }
}
