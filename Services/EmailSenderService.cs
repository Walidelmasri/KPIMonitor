using System;
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

        public async Task<(bool ok, string message)> SendEmailAsync(string to, string subject, string htmlBody)
        {
            try
            {
                var email = _cfg.GetSection("Email");
                var host      = email.GetValue<string>("Host");
                var port      = email.GetValue<int>("Port");
                var useSsl    = email.GetValue<bool>("UseSsl");
                var fromAddr  = email.GetValue<string>("FromAddress");
                var fromName  = email.GetValue<string>("FromName");
                var username  = email.GetValue<string>("Username"); // full mailbox, e.g. noreply@badea.org
                var password  = email.GetValue<string>("Password");

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = useSsl,                         // Office365: true on 587 (STARTTLS)
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(username, password),
                    Timeout = 15000
                };

                using var msg = new MailMessage
                {
                    From = new MailAddress(fromAddr, fromName),
                    Subject = subject ?? string.Empty,
                    Body = htmlBody ?? string.Empty,
                    IsBodyHtml = true
                };
                msg.To.Add(to);

                await client.SendMailAsync(msg);

                var okMsg = $"Email sent to {to}.";
                _log.LogInformation(okMsg);
                return (true, okMsg);
            }
            catch (SmtpException ex)
            {
                var err = $"SMTP error sending to {to}: {ex.StatusCode} {ex.Message}";
                _log.LogError(ex, err);
                return (false, err);
            }
            catch (Exception ex)
            {
                var err = $"Error sending to {to}: {ex.Message}";
                _log.LogError(ex, err);
                return (false, err);
            }
        }
    }
}
