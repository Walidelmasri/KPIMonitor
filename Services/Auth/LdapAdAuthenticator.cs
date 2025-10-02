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
        /// Validate credentials against AD and return normalized login in the
        /// original, working format: NETBIOS\sam (e.g., "BADEA\jdoe").
        /// We bind with the user's own credentials (no service account).
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
            var upnSuf  = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            // If user typed bare "jdoe", bind as "jdoe@badea.local"
            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation(
                "LDAP bind to {Server}:{Port} as {User} (SSL={Ssl}, AuthType={Auth})",
                server, port, bindUser, useSsl, authType);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // Bind with the user's credentials
                conn.Bind(new NetworkCredential(bindUser, password));

                // Keep the exact pattern you were using: NETBIOS\sam
                var sam = ExtractSam(bindUser);  // preserve casing the user typed for SAM
                var normalized = $"{netbios}\\{sam}";

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
        /// Check if a user (identified by SAM) is a member of the allowed AD group (nested membership supported).
        /// We bind with the SAME user credentials (no service account).
        /// Configure either Ad:AllowedGroupDn or Ad:AllowedGroupSam ("Steervision" by default).
        /// </summary>
        public async Task<bool> IsMemberOfAllowedGroupAsync(string usernameOrSam, string password, CancellationToken ct = default)
        {
            var ad     = _cfg.GetSection("Ad");
            var server = ad.GetValue<string>("Server") ?? "badea.local";
            var port   = ad.GetValue<int?>("Port") ?? 389;
            var useSsl = ad.GetValue<bool?>("UseSsl") ?? false;

            var baseDn = ad.GetValue<string>("BaseDn") ?? "DC=badea,DC=local";
            var grpSam = ad.GetValue<string>("AllowedGroupSam") ?? "Steervision"; // default to your group
            var grpDn  = ad.GetValue<string>("AllowedGroupDn");                   // optional exact DN
            var upnSuf = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            // Normalize: we always operate on SAM for identity equality (same as cookie Name value parsing below)
            var sam = ExtractSam(usernameOrSam);
            var bindUser = sam + upnSuf; // bind as UPN constructed from that SAM, consistent every time

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation("GROUP-CHECK start sam={Sam} bindUser={BindUser}", sam, bindUser);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // Bind as the user
                conn.Bind(new NetworkCredential(bindUser, password));

                // 1) Resolve user DN
                var userDn = await FindUserDnAsync(conn, baseDn, bindUser, ct);
                if (userDn is null)
                {
                    _log.LogWarning("User DN not found for {User}.", bindUser);
                    return false;
                }
                _log.LogInformation("GROUP-CHECK userDn={UserDn}", userDn);

                // 2) Resolve group DN then check nested membership
                string? groupDn = !string.IsNullOrWhiteSpace(grpDn)
                    ? grpDn
                    : await FindGroupDnBySamAsync(conn, baseDn, grpSam!, ct);

                if (string.IsNullOrWhiteSpace(groupDn))
                {
                    _log.LogWarning("Group not found. SAM={Sam}, DN={Dn}", grpSam, grpDn);
                    return false;
                }
                _log.LogInformation("GROUP-CHECK groupDn={GroupDn}", groupDn);

                // Nested membership check (LDAP_MATCHING_RULE_IN_CHAIN)
                string filter = $"(&(distinguishedName={Escape(groupDn)})(member:1.2.840.113556.1.4.1941:={Escape(userDn)}))";
                var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
                var res = (SearchResponse)await Task.Factory.FromAsync(
                    conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

                bool ok = res.Entries.Count > 0;
                _log.LogInformation("GROUP-CHECK result user={User} inGroup={InGroup}", bindUser, ok);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Group membership check failed for {User}", bindUser);
                return false;
            }
        }

        // -------- LDAP helpers --------

        private static string ExtractSam(string user)
        {
            // Accepts NETBIOS\sam, sam@domain, or bare sam and returns "sam"
            var bs = user.LastIndexOf('\\');
            if (bs >= 0 && bs < user.Length - 1) return user[(bs + 1)..];
            var at = user.IndexOf('@');
            if (at > 0) return user[..at];
            return user;
        }

        private static async Task<string?> FindUserDnAsync(LdapConnection conn, string baseDn, string userLogin, CancellationToken ct)
        {
            string sam = ExtractSam(userLogin);
            string filter = $"(|(sAMAccountName={Escape(sam)})(userPrincipalName={Escape(userLogin)}))";

            var req = new SearchRequest(baseDn, filter, SearchScope.Subtree, new[] { "distinguishedName" });
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);

            foreach (SearchResultEntry entry in res.Entries)
            {
                var dn = entry.DistinguishedName;
                if (!string.IsNullOrEmpty(dn)) return dn;
            }
            return null;
        }

        private static async Task<string?> FindGroupDnBySamAsync(LdapConnection conn, string baseDn, string groupSam, CancellationToken ct)
        {
            string grpFilter = $"(&(objectClass=group)(sAMAccountName={Escape(groupSam)}))";
            var grpReq = new SearchRequest(baseDn, grpFilter, SearchScope.Subtree, new[] { "distinguishedName" });

            var grpRes = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, grpReq, PartialResultProcessing.NoPartialResultSupport, null);

            if (grpRes.Entries.Count == 0) return null;
            return grpRes.Entries[0].DistinguishedName;
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
