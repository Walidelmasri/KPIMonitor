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

        /// <summary>
        /// Validate credentials against AD and return normalized login
        /// EXACTLY in your original format: NETBIOS\sam (e.g., "BADEA\jdoe").
        /// </summary>
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

            // Normalize input to NETBIOS\sam for binding and return
            var (domain, sam) = SplitDomainSam(username, netbios);
            var bindLogin = $"{domain}\\{sam}";

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Ntlm; // NTLM for non-windows still works for simple binds to AD

            _log.LogInformation("LDAP bind to {Server}:{Port} as {User} (SSL={Ssl}, AuthType={Auth})",
                server, port, bindLogin, useSsl, authType);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // For domain\user, pass domain separately in NetworkCredential
                var cred = new NetworkCredential(sam, password, domain);
                conn.Bind(cred);

                var normalized = $"{domain}\\{sam}";
                _log.LogInformation("LDAP bind OK. Normalized user = {User}", normalized);
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

        /// <summary>
        /// Check membership in the allowed AD group (nested membership supported).
        /// Binds with the SAME credentials (domain\sam + password).
        /// Configure Ad:AllowedGroupDn (preferred) or Ad:AllowedGroupSam (e.g., "Steervision").
        /// </summary>
        public async Task<bool> IsMemberOfAllowedGroupAsync(string usernameOrSamOrDomainSam, string password, CancellationToken ct = default)
        {
            var ad      = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";

            var baseDn  = ad.GetValue<string>("BaseDn") ?? "DC=badea,DC=local";
            var grpSam  = ad.GetValue<string>("AllowedGroupSam");                 // e.g., "Steervision"
            var grpDn   = ad.GetValue<string>("AllowedGroupDn");                  // e.g., "CN=Steervision,OU=...,DC=..."
            if (string.IsNullOrWhiteSpace(grpDn) && string.IsNullOrWhiteSpace(grpSam))
            {
                _log.LogError("AD group not configured. Set Ad:AllowedGroupDn or Ad:AllowedGroupSam.");
                return false;
            }

            // Normalize to DOMAIN\sam for binding and for equality
            var (domain, sam) = SplitDomainSam(usernameOrSamOrDomainSam, netbios);
            var bindLogin = $"{domain}\\{sam}";

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Ntlm;

            _log.LogInformation("GROUP-CHECK start bindLogin={BindLogin} baseDn={BaseDn} grpSam={GrpSam} grpDn={GrpDn}",
                bindLogin, baseDn, grpSam, grpDn);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                var cred = new NetworkCredential(sam, password, domain);
                conn.Bind(cred);

                // 1) Find the user's DN via sAMAccountName
                var userDn = await FindUserDnBySamAsync(conn, baseDn, sam, ct);
                if (userDn is null)
                {
                    _log.LogWarning("User DN not found for sam={Sam}.", sam);
                    return false;
                }
                _log.LogInformation("GROUP-CHECK userDn={UserDn}", userDn);

                // 2) Resolve group DN
                string? groupDn = !string.IsNullOrWhiteSpace(grpDn)
                    ? grpDn
                    : await FindGroupDnBySamAsync(conn, baseDn, grpSam!, ct);

                if (string.IsNullOrWhiteSpace(groupDn))
                {
                    _log.LogWarning("Group not found. grpSam={GrpSam}, grpDn={GrpDn}", grpSam, grpDn);
                    return false;
                }
                _log.LogInformation("GROUP-CHECK groupDn={GroupDn}", groupDn);

                // 3) Nested membership via LDAP_MATCHING_RULE_IN_CHAIN
                string filter = $"(&(distinguishedName={Escape(groupDn)})(member:1.2.840.113556.1.4.1941:={Escape(userDn)}))";
                var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
                var res = (SearchResponse)await Task.Factory.FromAsync(
                    conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

                bool ok = res.Entries.Count > 0;
                _log.LogInformation("GROUP-CHECK result bindLogin={BindLogin} inGroup={InGroup}", bindLogin, ok);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Group membership check failed for {User}", bindLogin);
                return false;
            }
        }

        // ---------- helpers ----------

        private static (string domain, string sam) SplitDomainSam(string input, string defaultDomain)
        {
            if (string.IsNullOrWhiteSpace(input))
                return (defaultDomain, "");

            var trimmed = input.Trim();

            // DOMAIN\sam
            var slash = trimmed.IndexOf('\\');
            if (slash > 0 && slash < trimmed.Length - 1)
                return (trimmed[..slash], trimmed[(slash + 1)..]);

            // sam@domain -> we still return NETBIOS/default domain
            var at = trimmed.IndexOf('@');
            if (at > 0)
            {
                var sam = trimmed[..at];
                return (defaultDomain, sam);
            }

            // bare sam
            return (defaultDomain, trimmed);
        }

        private static async Task<string?> FindUserDnBySamAsync(LdapConnection conn, string baseDn, string sam, CancellationToken ct)
        {
            string filter = $"(&(objectClass=user)(sAMAccountName={Escape(sam)}))";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

            foreach (SearchResultEntry entry in res.Entries)
                return entry.DistinguishedName;

            return null;
        }

        private static async Task<string?> FindGroupDnBySamAsync(LdapConnection conn, string baseDn, string groupSam, CancellationToken ct)
        {
            string filter = $"(&(objectClass=group)(sAMAccountName={Escape(groupSam)}))";
            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });

            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

            foreach (SearchResultEntry entry in res.Entries)
                return entry.DistinguishedName;

            return null;
        }

        // Minimal DN filter escaping
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
                    default:   sb.Append(c);      break;
                }
            }
            return sb.ToString();
        }
    }
}
