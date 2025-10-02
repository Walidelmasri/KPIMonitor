using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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

        // ---------------- LOGIN (unchanged behavior) ----------------
        public Task<string?> ValidateAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                _log.LogWarning("Empty username or password.");
                return Task.FromResult<string?>(null);
            }

            var ad      = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";

            var (domain, sam) = SplitDomainSam(username, netbios);
            var bindLogin = $"{domain}\\{sam}";

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? AuthType.Negotiate : AuthType.Ntlm;
            _log.LogInformation("LOGIN bind {Server}:{Port} as {User} (SSL={Ssl}, AuthType={Auth})",
                server, port, bindLogin, useSsl, authType);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                conn.Bind(new NetworkCredential(sam, password, domain));

                var normalized = $"{domain}\\{sam}";
                _log.LogInformation("LOGIN OK. Normalized user = {User}", normalized);
                return Task.FromResult<string?>(normalized);
            }
            catch (LdapException lex)
            {
                _log.LogWarning(lex, "LOGIN failed. ErrorCode={Code}, ServerError={ServerError}", lex.ErrorCode, lex.ServerErrorMessage);
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "LOGIN unexpected error.");
                return Task.FromResult<string?>(null);
            }
        }

        // ------------- GROUP CHECK (service bind if available) --------------
        public async Task<bool> IsMemberOfAllowedGroupAsync(string usernameOrDomainSam, string? userPassword, CancellationToken ct = default)
        {
            var ad      = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";
            var baseDn  = ad.GetValue<string>("BaseDn") ?? "DC=badea,DC=local";
            var grpSam  = ad.GetValue<string>("AllowedGroupSam");
            var grpDn   = ad.GetValue<string>("AllowedGroupDn");

            var svcSam  = ad.GetValue<string>("BindUserSam");         // optional
            var svcPwd  = ad.GetValue<string>("BindUserPassword");    // optional

            if (string.IsNullOrWhiteSpace(grpSam) && string.IsNullOrWhiteSpace(grpDn))
            {
                _log.LogError("Group not configured. Set Ad:AllowedGroupSam or Ad:AllowedGroupDn.");
                return false;
            }

            // Who are we checking?
            var (_, samToCheck) = SplitDomainSam(usernameOrDomainSam, netbios);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);

                using var conn = new LdapConnection(id);
                var authType = AuthType.Negotiate;

                // Choose bind identity (prefer service account)
                if (!string.IsNullOrWhiteSpace(svcSam) && !string.IsNullOrWhiteSpace(svcPwd))
                {
                    _log.LogInformation("GROUP bind using service account '{SvcSam}'", svcSam);
                    conn.AuthType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? AuthType.Negotiate : AuthType.Ntlm;
                    conn.SessionOptions.SecureSocketLayer = useSsl;
                    conn.Bind(new NetworkCredential(svcSam, svcPwd, netbios));
                }
                else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _log.LogInformation("GROUP bind using Negotiate with process identity (IIS app-pool).");
                    conn.AuthType = AuthType.Negotiate;
                    conn.SessionOptions.SecureSocketLayer = useSsl;
                    conn.Bind(); // use current process token
                }
                else
                {
                    // Last resort: user’s own password if provided
                    if (string.IsNullOrEmpty(userPassword))
                    {
                        _log.LogWarning("GROUP bind has no service account and no user password. Cannot query AD.");
                        return false;
                    }
                    _log.LogInformation("GROUP bind using user '{Sam}' (fallback).", samToCheck);
                    conn.AuthType = AuthType.Ntlm;
                    conn.SessionOptions.SecureSocketLayer = useSsl;
                    conn.Bind(new NetworkCredential(samToCheck, userPassword, netbios));
                }

                // Resolve user DN
                var userDn = await FindUserDnBySamAsync(conn, baseDn, samToCheck, ct);
                if (userDn is null)
                {
                    _log.LogWarning("GROUP: user DN not found for sam={Sam}.", samToCheck);
                    return false;
                }

                // Resolve group DN (by DN or by SAM)
                string? groupDn = !string.IsNullOrWhiteSpace(grpDn)
                    ? grpDn
                    : await FindGroupDnBySamAsync(conn, baseDn, grpSam!, ct);

                if (string.IsNullOrWhiteSpace(groupDn))
                {
                    _log.LogWarning("GROUP: group not found. grpSam={GrpSam}, grpDn={GrpDn}", grpSam, grpDn);
                    return false;
                }

                // Nested membership check
                string filter = $"(&(distinguishedName={Escape(groupDn)})(member:1.2.840.113556.1.4.1941:={Escape(userDn)}))";
                var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
                var res = (SearchResponse)await Task.Factory.FromAsync(
                    conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

                bool ok = res.Entries.Count > 0;
                _log.LogInformation("GROUP result sam={Sam} inGroup={InGroup}", samToCheck, ok);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "GROUP membership check failed for {User}", usernameOrDomainSam);
                return false;
            }
        }

        // ---------------- helpers ----------------
        private static (string domain, string sam) SplitDomainSam(string input, string defaultDomain)
        {
            var s = (input ?? "").Trim();
            var i = s.IndexOf('\\');
            if (i > 0 && i < s.Length - 1) return (s[..i], s[(i + 1)..]);
            var at = s.IndexOf('@');
            if (at > 0) return (defaultDomain, s[..at]);
            return (defaultDomain, s);
        }

        private static async Task<string?> FindUserDnBySamAsync(LdapConnection conn, string baseDn, string sam, CancellationToken ct)
        {
            string filter = $"(&(objectClass=user)(sAMAccountName={Escape(sam)}))";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

            foreach (SearchResultEntry e in res.Entries) return e.DistinguishedName;
            return null;
        }

        private static async Task<string?> FindGroupDnBySamAsync(LdapConnection conn, string baseDn, string groupSam, CancellationToken ct)
        {
            string filter = $"(&(objectClass=group)(sAMAccountName={Escape(groupSam)}))";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

            foreach (SearchResultEntry e in res.Entries) return e.DistinguishedName;
            return null;
        }

        private static string Escape(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\5c"); break;
                    case '*':  sb.Append("\\2a"); break;
                    case '(':  sb.Append("\\28"); break;
                    case ')':  sb.Append("\\29"); break;
                    case '\0': sb.Append("\\00"); break;
                    default:   sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
