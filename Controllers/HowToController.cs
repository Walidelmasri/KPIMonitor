using Microsoft.AspNetCore.Mvc;

namespace KPIMonitor.Controllers
{
    public class HowToController : Controller
    {
        [HttpGet]
        public IActionResult Index() => View(); // purely static page
    }
}