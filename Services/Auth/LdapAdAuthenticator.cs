using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace KPIMonitor.Services.Auth
{
    public sealed class LdapAdAuthenticator : IAdAuthenticator
    {
        private readonly IConfiguration _cfg;
        private readonly ILogger<LdapAdAuthenticator> _log;

        public LdapAdAuthenticator(IConfiguration cfg, ILogger<LdapAdAuthenticator> log)
        {
            _cfg = cfg;
            _log = log;
        }

        public Task<string?> ValidateAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                _log.LogWarning("Empty username or password.");
                return Task.FromResult<string?>(null);
            }

            var ad = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";
            var upnSuf  = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            // Normalize: if user typed bare "jdoe", bind as "jdoe@badea.local"
            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            // AuthType: Negotiate only on Windows; Basic elsewhere
            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation(
                "Attempting LDAP bind to {Server}:{Port} as {User} (SSL={Ssl}, AuthType={Auth}, OS={OS})",
                server, port, bindUser, useSsl, authType, RuntimeInformation.OSDescription);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id)
                {
                    AuthType = authType
                };

                // LDAPS if configured
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // (Optional) If you later want StartTLS on port 389:
                // if (!useSsl) conn.SessionOptions.StartTransportLayerSecurity(null);

                // Bind (throws on invalid credentials or connectivity issues)
                conn.Bind(new NetworkCredential(bindUser, password));

                // Success → normalize to DOMAIN\sam
                var sam = ExtractSam(bindUser);
                var normalized = $"{netbios}\\{sam}";

                _log.LogInformation("LDAP bind successful for {User}.", normalized);
                return Task.FromResult<string?>(normalized);
            }
            catch (LdapException lex)
            {
                _log.LogWarning(lex, "LDAP bind failed. ErrorCode={Code}, ServerError={ServerError}", lex.ErrorCode, lex.ServerErrorMessage);
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected error during LDAP bind.");
                return Task.FromResult<string?>(null);
            }
        }

        private static string ExtractSam(string user)
        {
            var bs = user.LastIndexOf('\\');
            if (bs >= 0 && bs < user.Length - 1) return user[(bs + 1)..];
            var at = user.IndexOf('@');
            if (at > 0) return user[..at];
            return user;
        }
    }
}
