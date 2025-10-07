using System.Threading.Tasks;

namespace KPIMonitor.Services.Abstractions
{
    public interface IEmailSender
    {
        Task SendEmailAsync(string to, string subject, string body);
    }
}
