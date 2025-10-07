using System.Threading.Tasks;

namespace KPIMonitor.Services.Abstractions
{
public interface IEmailSender
{
    Task<(bool ok, string message)> SendEmailAsync(string to, string subject, string htmlBody);
}

}
