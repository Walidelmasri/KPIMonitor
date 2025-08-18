using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class KpiDataController : Controller
    {
        public IActionResult AddKpiData()
        {
            return View();
        }
    }
}
