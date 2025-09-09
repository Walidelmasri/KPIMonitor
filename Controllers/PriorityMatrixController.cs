using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Services.Abstractions;

namespace KPIMonitor.Controllers
{
    public class PriorityMatrixController : Controller
    {
        private readonly IPriorityMatrixService _svc;
        public PriorityMatrixController(IPriorityMatrixService svc) => _svc = svc;

        [HttpGet]
        public async Task<IActionResult> Index(int? year = null)
        {
            var vm = await _svc.BuildAsync(year);
            ViewData["Title"] = "Priority Matrix";
            return View(vm);
        }
    }
}
