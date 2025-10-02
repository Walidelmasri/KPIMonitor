using System.DirectoryServices.Protocols;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;

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
            var server = ad.GetValue<string>("Server") ?? "badea.local";
            var port = ad.GetValue<int?>("Port") ?? 389;
            var useSsl = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";
            var upnSuf = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation(
                "Attempting LDAP bind to {Server}:{Port} as {User} (SSL={Ssl}, AuthType={Auth})",
                server, port, bindUser, useSsl, authType);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                conn.Bind(new NetworkCredential(bindUser, password));

                var sam = ExtractSam(bindUser).ToLowerInvariant();

                _log.LogInformation("LDAP bind successful for SAM {User}.", sam);
                return Task.FromResult<string?>(sam);

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

        public async Task<bool> IsMemberOfAllowedGroupAsync(string username, string password, CancellationToken ct = default)
        {
            // Config
            var ad = _cfg.GetSection("Ad");
            var server = ad.GetValue<string>("Server") ?? "badea.local";
            var port = ad.GetValue<int?>("Port") ?? 389;
            var useSsl = ad.GetValue<bool?>("UseSsl") ?? false;
            var baseDn = ad.GetValue<string>("BaseDn") ?? "DC=badea,DC=local";
            var grpSam = ad.GetValue<string>("AllowedGroupSam");
            var grpDn = ad.GetValue<string>("AllowedGroupDn");
            var upnSuf = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            if (string.IsNullOrWhiteSpace(grpSam) && string.IsNullOrWhiteSpace(grpDn))
            {
                _log.LogError("AD group not configured. Set Ad:AllowedGroupSam or Ad:AllowedGroupDn.");
                return false;
            }

            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // bind as the user (so no service account needed)
                conn.Bind(new NetworkCredential(bindUser, password));

                // 1) Find user DN
                var userDn = await FindUserDnAsync(conn, baseDn, bindUser, ct);
                if (userDn is null)
                {
                    _log.LogWarning("User DN not found for {User}.", bindUser);
                    return false;
                }

                // 2) Check membership
                if (!string.IsNullOrWhiteSpace(grpDn))
                {
                    // Check by group DN
                    return await IsUserMemberOfGroupDnAsync(conn, baseDn, userDn, grpDn!, ct);
                }
                else
                {
                    // Check by group SAM
                    return await IsUserMemberOfGroupSamAsync(conn, baseDn, userDn, grpSam!, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Group membership check failed for {User}", bindUser);
                return false;
            }
        }

        // --- Helpers ---

        private static string ExtractSam(string user)
        {
            var bs = user.LastIndexOf('\\');
            if (bs >= 0 && bs < user.Length - 1) return user[(bs + 1)..];
            var at = user.IndexOf('@');
            if (at > 0) return user[..at];
            return user;
        }

        private static async Task<string?> FindUserDnAsync(LdapConnection conn, string baseDn, string userLogin, CancellationToken ct)
        {
            // Accept SAM or UPN in userLogin
            string sam = ExtractSam(userLogin);
            // filter tries both sAMAccountName and userPrincipalName
            string filter = $"(|(sAMAccountName={Escape(sam)})(userPrincipalName={Escape(userLogin)}))";

            var req = new SearchRequest(
                baseDn,
                filter,
                SearchScope.Subtree,
                new[] { "distinguishedName" }
            );

            var res = (SearchResponse)await Task.Factory.FromAsync(conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);
            foreach (SearchResultEntry entry in res.Entries)
            {
                var dn = entry.DistinguishedName;
                if (!string.IsNullOrEmpty(dn))
                    return dn;
            }
            return null;
        }

        private static async Task<bool> IsUserMemberOfGroupDnAsync(LdapConnection conn, string baseDn, string userDn, string groupDn, CancellationToken ct)
        {
            // Nested membership test via LDAP_MATCHING_RULE_IN_CHAIN (1.2.840.113556.1.4.1941)
            // Filter says: find the group whose DN matches and has member:1.2.840...:userDn
            string filter = $"(&(distinguishedName={Escape(groupDn)})(member:1.2.840.113556.1.4.1941:={Escape(userDn)}))";

            var req = new SearchRequest(
                baseDn,
                filter,
                SearchScope.Subtree,
                new[] { "distinguishedName" }
            );

            var res = (SearchResponse)await Task.Factory.FromAsync(conn.BeginSendRequest, conn.EndSendRequest, req, PartialResultProcessing.NoPartialResultSupport, null);
            return res.Entries.Count > 0;
        }

        private static async Task<bool> IsUserMemberOfGroupSamAsync(LdapConnection conn, string baseDn, string userDn, string groupSam, CancellationToken ct)
        {
            // First locate the group DN by sAMAccountName
            string grpFilter = $"(&(objectClass=group)(sAMAccountName={Escape(groupSam)}))";
            var grpReq = new SearchRequest(
                baseDn,
                grpFilter,
                SearchScope.Subtree,
                new[] { "distinguishedName" }
            );
            var grpRes = (SearchResponse)await Task.Factory.FromAsync(conn.BeginSendRequest, conn.EndSendRequest, grpReq, PartialResultProcessing.NoPartialResultSupport, null);
            if (grpRes.Entries.Count == 0) return false;

            string groupDn = grpRes.Entries[0].DistinguishedName;
            return await IsUserMemberOfGroupDnAsync(conn, baseDn, userDn, groupDn, ct);
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
                    case '*': sb.Append("\\2a"); break;
                    case '(': sb.Append("\\28"); break;
                    case ')': sb.Append("\\29"); break;
                    case '\0': sb.Append("\\00"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }
}
