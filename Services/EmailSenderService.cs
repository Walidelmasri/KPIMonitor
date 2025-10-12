using System;
using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
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
                // Hard sanitize headers/addresses to avoid "invalid character '@' in header"
                to = SanitizeAddress(to);
                subject = SanitizeHeader(subject);

                var emailSection = _cfg.GetSection("Email");
                var host     = emailSection.GetValue<string>("Host");
                var port     = emailSection.GetValue<int>("Port");
                var useSsl   = emailSection.GetValue<bool>("UseSsl");
                var fromAddr = SanitizeAddress(emailSection.GetValue<string>("FromAddress"));
                var fromName = SanitizeHeader(emailSection.GetValue<string>("FromName") ?? "KPI Monitor");
                var username = emailSection.GetValue<string>("Username");
                var password = emailSection.GetValue<string>("Password");

                using var client = new SmtpClient(host, port)
                {
                    EnableSsl = useSsl,
                    Credentials = new NetworkCredential(username, password),
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    Timeout = 20000
                };

                using var msg = new MailMessage
                {
                    From = new MailAddress(fromAddr, fromName),
                    Subject = subject,
                    Body = htmlBody ?? "",
                    IsBodyHtml = true
                };
                msg.To.Add(new MailAddress(to));

                await client.SendMailAsync(msg);
                _log.LogInformation("Email sent to {To}", to);
                return (true, "sent");
            }
            catch (SmtpException ex)
            {
                _log.LogError(ex, "SMTP error sending to {To}", to);
                return (false, $"SMTP: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Error sending to {To}", to);
                return (false, ex.Message);
            }
        }

        private static string SanitizeHeader(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            // strip CR/LF and control chars
            s = s.Replace("\r", "").Replace("\n", "").Trim();
            return s;
        }

        private static string SanitizeAddress(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            s = s.Trim();
            s = s.Replace("\r", "").Replace("\n", "");

            // If the directory already gave an email, accept it; if it gave SAM, append once.
            // Also collapse accidental double domains like "user@badea.org@badea.org".
            var m = Regex.Match(s, @"^[^@\s]+@[^@\s]+$");
            if (m.Success) return s; // already an email

            // treat as login/SAM
            if (s.Contains("\\"))
            {
                var bs = s.LastIndexOf('\\');
                if (bs >= 0 && bs < s.Length - 1) s = s[(bs + 1)..];
            }
            if (s.Contains("@"))
            {
                // Looks like an email but maybe with extra text — keep only the email-ish part
                var parts = s.Split(' ', '\t', ';', ',');
                foreach (var p in parts)
                {
                    if (Regex.IsMatch(p, @"^[^@\s]+@[^@\s]+$")) return p;
                }
                // fallback: remove second @ if any
                var idx = s.IndexOf('@');
                if (idx >= 0) s = s[..(idx)];
            }
            return $"{s}@badea.org";
        }
    }
}
