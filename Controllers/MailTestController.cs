using Microsoft.AspNetCore.Mvc;
using KPIMonitor.Services.Abstractions;
using System.Threading.Tasks;

namespace KPIMonitor.Controllers
{
    [Route("[controller]/[action]")]
    public class MailTestController : Controller
    {
        private readonly IEmailSender _email;

        public MailTestController(IEmailSender email)
        {
            _email = email;
        }

        [HttpGet]
        public async Task<IActionResult> SendTest(string to = "walid.salem@badea.org")
        {
            var subject = "KPI Monitor SMTP Test";
            var body = $"✅ Test message from KPI Monitor at {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}.";

            await _email.SendEmailAsync(to, subject, body);
            return Content($"Email sent successfully to {to}");
        }
    }
}
