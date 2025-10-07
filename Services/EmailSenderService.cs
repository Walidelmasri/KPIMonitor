using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using KPIMonitor.Services.Abstractions;

namespace KPIMonitor.Services
{
    public sealed class EmailSenderService : IEmailSender
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<EmailSenderService> _log;

        public EmailSenderService(IConfiguration cfg, ILogger<EmailSenderService> log)
        {
            _cfg = cfg;
            _log = log;
        }

        public async Task SendEmailAsync(string to, string subject, string body)
        {
            var emailSection = _cfg.GetSection("Email");
            var host = emailSection.GetValue<string>("Host");
            var port = emailSection.GetValue<int>("Port");
            var useSsl = emailSection.GetValue<bool>("UseSsl");
            var fromAddr = emailSection.GetValue<string>("FromAddress");
            var fromName = emailSection.GetValue<string>("FromName");
            var username = emailSection.GetValue<string>("Username");
            var password = emailSection.GetValue<string>("Password");

            using var client = new SmtpClient(host, port)
            {
                EnableSsl = useSsl,
                Credentials = new NetworkCredential(username, password),
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 10000
            };

            var msg = new MailMessage
            {
                From = new MailAddress(fromAddr, fromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            msg.To.Add(to);

            try
            {
                await client.SendMailAsync(msg);
                _log.LogInformation("Email sent successfully to {To}.", to);
            }
            catch (SmtpException ex)
            {
                _log.LogError(ex, "SMTP failed sending email to {To}.", to);
                throw;
            }
        }
    }
}
