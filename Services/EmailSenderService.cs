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
            try
            {
                var emailSection = _cfg.GetSection("Email");
                var host     = emailSection.GetValue<string>("Host") ?? "localhost";
                var port     = emailSection.GetValue<int?>("Port") ?? 25;
                var useSsl   = emailSection.GetValue<bool?>("UseSsl") ?? false;
                var fromAddr = emailSection.GetValue<string>("FromAddress") ?? "kpi-monitor@badea.org";
                var fromName = emailSection.GetValue<string>("FromName") ?? "BADEA KPI Monitor";
                var username = emailSection.GetValue<string>("Username");
                var password = emailSection.GetValue<string>("Password");

                // Extract address if config used "Name <addr>" format
                fromAddr = ExtractEmailAddress(fromAddr);
                if (string.IsNullOrWhiteSpace(fromAddr) || !fromAddr.Contains("@"))
                    return (false, "Invalid FromAddress in configuration.");

                // Ensure display name has no '@' or control chars
                fromName = SanitizeDisplayName(fromName);

                // No CR/LF or control chars in subject (header injection guard)
                subject = SanitizeHeader(subject);

                using var msg = new MailMessage
                {
                    From            = new MailAddress(fromAddr, fromName, Encoding.UTF8),
                    Subject         = subject,
                    SubjectEncoding = Encoding.UTF8,
                    Body            = htmlBody ?? string.Empty,
                    BodyEncoding    = Encoding.UTF8,
                    IsBodyHtml      = true,
                    HeadersEncoding = Encoding.UTF8
                };

                // Support comma/semicolon separated recipients; accept "Name <a@b.com>" or "a@b.com"
                var recipients = SplitRecipients(to);
                foreach (var r in recipients)
                {
                    var addr = ExtractEmailAddress(r);
                    if (!string.IsNullOrWhiteSpace(addr) && addr.Contains("@"))
                        msg.To.Add(new MailAddress(addr)); // no display name here
                }
                if (msg.To.Count == 0)
                    return (false, "No valid recipient.");

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl      = useSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network
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

                await client.SendMailAsync(msg);
                _log.LogInformation("Email sent to {To}", string.Join(", ", msg.To.Select(x => x.Address)));
                return (true, $"Email sent to {string.Join(", ", msg.To.Select(x => x.Address))}");
            }
            catch (SmtpException ex)
            {
                _log.LogError(ex, "SMTP error sending email.");
                return (false, $"SMTP failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled error sending email.");
                return (false, $"Send failed: {ex.Message}");
            }
        }

        private static string[] SplitRecipients(string to) =>
            (to ?? "")
                .Replace("\r", "").Replace("\n", "")
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

        // Accepts "Name <user@domain>" or "user@domain", returns "user@domain"
        private static string ExtractEmailAddress(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            var s = input.Replace("\r", "").Replace("\n", "").Trim();
            var lt = s.IndexOf('<');
            var gt = s.IndexOf('>');
            if (lt >= 0 && gt > lt) s = s.Substring(lt + 1, gt - lt - 1);
            return s.Trim(' ', '\t', '"', '\'');
        }
    }
}
