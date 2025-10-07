using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Services.Abstractions;
using System.Threading.Tasks;

namespace KPIMonitor.Controllers
{
    public class MailTestController : Controller
    {
        private readonly IEmailSender _email;

        public MailTestController(IEmailSender email)
        {
            _email = email;
        }

        [HttpGet]
        public IActionResult Send()
        {
            ViewBag.Result = TempData["Result"] as string;
            ViewBag.Error = TempData["Error"] as string;
            ViewBag.DefaultTo = "walid.salem@badea.org";
            return View();
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Send(string to, string subject, string body)
        {
            var (ok, message) = await _email.SendEmailAsync(to, subject, body);
            if (ok) TempData["Result"] = message;
            else    TempData["Error"]  = message;
            return RedirectToAction(nameof(Send));
        }
    }
}
