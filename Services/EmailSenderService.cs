using System;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;
using KPIMonitor.Services.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

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
            var emailSection = _cfg.GetSection("Email");
            var host     = emailSection.GetValue<string>("Host") ?? "localhost";
            var port     = emailSection.GetValue<int?>("Port") ?? 25;
            var useSsl   = emailSection.GetValue<bool?>("UseSsl") ?? false;
            var fromAddr = emailSection.GetValue<string>("FromAddress") ?? "kpi-monitor@badea.org";
            var fromName = emailSection.GetValue<string>("FromName") ?? "BADEA KPI Monitor";
            var username = emailSection.GetValue<string>("Username");
            var password = emailSection.GetValue<string>("Password");

            // Display name must NOT contain '@' or control chars
            fromName = SanitizeDisplayName(fromName);
            // Subjects must not contain CR/LF/control chars
            subject  = SanitizeHeader(subject);

            using var msg = new MailMessage
            {
                From             = new MailAddress(fromAddr, fromName, Encoding.UTF8),
                Subject          = subject,
                SubjectEncoding  = Encoding.UTF8,
                Body             = htmlBody ?? string.Empty,
                BodyEncoding     = Encoding.UTF8,
                IsBodyHtml       = true,
                HeadersEncoding  = Encoding.UTF8
            };

            foreach (var addr in SplitRecipients(to))
                msg.To.Add(new MailAddress(addr)); // no display names for recipients

            using var client = new SmtpClient(host, port)
            {
                EnableSsl       = useSsl,
                DeliveryMethod  = SmtpDeliveryMethod.Network
            };

            if (!string.IsNullOrWhiteSpace(username))
            {
                client.UseDefaultCredentials = false;
                client.Credentials = new NetworkCredential(username, password);
            }
            else
            {
                client.UseDefaultCredentials = true;
            }

            try
            {
                await client.SendMailAsync(msg);
                _log.LogInformation("Email sent to {To}", to);
                return (true, $"Email sent to {to}");
            }
            catch (SmtpException ex)
            {
                _log.LogError(ex, "SMTP error sending to {To}", to);
                return (false, $"SMTP failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error sending to {To}", to);
                return (false, $"Send failed: {ex.Message}");
            }
        }

        private static string[] SplitRecipients(string to) =>
            (to ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToArray();

        private static string SanitizeHeader(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var cleaned = s.Replace("\r", "").Replace("\n", "");
            return new string(cleaned.Where(ch => !char.IsControl(ch)).ToArray());
        }

        private static string SanitizeDisplayName(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "BADEA KPI Monitor";
            var cleaned = new string(s.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
            return cleaned.Contains('@') ? "BADEA KPI Monitor" : cleaned;
        }
    }
}
