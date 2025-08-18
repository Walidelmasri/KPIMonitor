using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Models;

namespace KPIMonitor.Controllers
{
    public class AccountController : Controller
    {
        public IActionResult Login()
        {
            return View(new LoginViewModel());
        }
    }
}
