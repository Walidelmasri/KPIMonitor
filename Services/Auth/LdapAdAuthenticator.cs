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

        /// <summary>
        /// Authenticates against LDAP/AD. On success returns the SAM (lowercased),
        /// exactly like your current behavior.
        /// </summary>
        public Task<string?> ValidateAsync(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password))
            {
                _log.LogWarning("ValidateAsync: empty username or password.");
                return Task.FromResult<string?>(null);
            }

            var ad      = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var netbios = ad.GetValue<string>("DomainNetbios") ?? "BADEA";
            var upnSuf  = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            // Normalize to a bindable identity (UPN if bare SAM was entered)
            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation(
                "ValidateAsync: binding to LDAP {Server}:{Port} as '{User}' (SSL={Ssl}, AuthType={Auth}, OS={OS})",
                server, port, bindUser, useSsl, authType, RuntimeInformation.OSDescription);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                conn.Bind(new NetworkCredential(bindUser, password));
                sw.Stop();

                var sam = ExtractSam(bindUser).ToLowerInvariant();
                _log.LogInformation("ValidateAsync: LDAP bind OK for '{User}' → sam='{Sam}' in {Ms} ms", bindUser, sam, sw.ElapsedMilliseconds);

                // NOTE: You previously changed this to return lowercased SAM; keeping that behavior.
                return Task.FromResult<string?>(sam);
            }
            catch (LdapException lex)
            {
                _log.LogWarning(lex, "ValidateAsync: LDAP bind FAILED for '{User}'. ErrorCode={Code}, ServerError={ServerError}",
                    bindUser, lex.ErrorCode, lex.ServerErrorMessage);
                return Task.FromResult<string?>(null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ValidateAsync: unexpected error for '{User}'.", bindUser);
                return Task.FromResult<string?>(null);
            }
        }

        /// <summary>
        /// Checks if the user is a member of the configured AD group (directly or nested).
        /// Uses user's own credentials for the LDAP bind (no service account).
        /// Returns true/false and logs every step so you can see exactly where it fails.
        /// </summary>
        public async Task<bool> IsMemberOfAllowedGroupAsync(string username, string password, CancellationToken ct = default)
        {
            // Config
            var ad      = _cfg.GetSection("Ad");
            var server  = ad.GetValue<string>("Server") ?? "badea.local";
            var port    = ad.GetValue<int?>("Port") ?? 389;
            var useSsl  = ad.GetValue<bool?>("UseSsl") ?? false;
            var baseDn  = ad.GetValue<string>("BaseDn") ?? "DC=badea,DC=local";
            var grpSam  = ad.GetValue<string>("AllowedGroupSam");
            var grpDn   = ad.GetValue<string>("AllowedGroupDn");
            var upnSuf  = ad.GetValue<string>("UserPrincipalSuffix") ?? "@badea.local";

            _log.LogInformation("GroupCheck: Config -> Server={Server}:{Port} SSL={Ssl} BaseDn='{BaseDn}' AllowedGroupSam='{Sam}' AllowedGroupDn='{Dn}'",
                server, port, useSsl, baseDn, grpSam ?? "(null)", grpDn ?? "(null)");

            if (string.IsNullOrWhiteSpace(grpSam) && string.IsNullOrWhiteSpace(grpDn))
            {
                _log.LogError("GroupCheck: No group configured. Set Ad:AllowedGroupSam or Ad:AllowedGroupDn.");
                return false;
            }

            // Normalize bind identity
            string bindUser = (username.Contains('\\') || username.Contains('@'))
                ? username
                : username + upnSuf;

            var authType = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? AuthType.Negotiate
                : AuthType.Basic;

            _log.LogInformation("GroupCheck: will bind as '{User}' (AuthType={Auth})", bindUser, authType);

            try
            {
                var id = new LdapDirectoryIdentifier(server, port, false, false);
                using var conn = new LdapConnection(id) { AuthType = authType };
                conn.SessionOptions.SecureSocketLayer = useSsl;

                // 0) Bind as the user (so we verify credentials again here)
                var swBind = System.Diagnostics.Stopwatch.StartNew();
                conn.Bind(new NetworkCredential(bindUser, password));
                swBind.Stop();
                _log.LogInformation("GroupCheck: bind OK for '{User}' in {Ms} ms", bindUser, swBind.ElapsedMilliseconds);

                // 1) Resolve user DN
                var swUser = System.Diagnostics.Stopwatch.StartNew();
                var userDn = await FindUserDnAsync(conn, baseDn, bindUser, ct);
                swUser.Stop();

                _log.LogInformation("GroupCheck: userDn = '{UserDn}' (lookup {Ms} ms)", userDn ?? "(null)", swUser.ElapsedMilliseconds);
                if (userDn is null)
                {
                    _log.LogWarning("GroupCheck: user DN NOT FOUND for '{User}'.", bindUser);
                    return false;
                }

                bool result;

                // 2) Check membership via DN or SAM, logging exact path
                if (!string.IsNullOrWhiteSpace(grpDn))
                {
                    _log.LogInformation("GroupCheck: using group DN = '{GroupDn}'", grpDn);
                    var swCheck = System.Diagnostics.Stopwatch.StartNew();
                    result = await IsUserMemberOfGroupDnAsync(conn, baseDn, userDn, grpDn!, ct);
                    swCheck.Stop();
                    _log.LogInformation("GroupCheck: DN membership result = {Result} (elapsed {Ms} ms)", result, swCheck.ElapsedMilliseconds);
                }
                else
                {
                    _log.LogInformation("GroupCheck: using group SAM = '{GroupSam}'", grpSam);
                    var swCheck = System.Diagnostics.Stopwatch.StartNew();
                    result = await IsUserMemberOfGroupSamAsync(conn, baseDn, userDn, grpSam!, ct);
                    swCheck.Stop();
                    _log.LogInformation("GroupCheck: SAM membership result = {Result} (elapsed {Ms} ms)", result, swCheck.ElapsedMilliseconds);
                }

                return result;
            }
            catch (LdapException lex)
            {
                _log.LogWarning(lex, "GroupCheck: LDAP error for '{User}'. ErrorCode={Code}, ServerError={ServerError}",
                    bindUser, lex.ErrorCode, lex.ServerErrorMessage);
                return false;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "GroupCheck: unexpected error for '{User}'.", bindUser);
                return false;
            }
        }

        // ----------------- Helpers -----------------

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
            string sam = ExtractSam(userLogin);

            // Try both SAM and UPN
            string filter = $"(|(sAMAccountName={Escape(sam)})(userPrincipalName={Escape(userLogin)}))";
            var req = new SearchRequest(
                baseDn,
                filter,
                SearchScope.Subtree,
                new[] { "distinguishedName", "sAMAccountName", "userPrincipalName" }
            );

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req,
                PartialResultProcessing.NoPartialResultSupport, null);
            sw.Stop();

            if (res.Entries.Count == 0)
            {
                return null;
            }

            // Log the first match + how many returned
            var dn = res.Entries[0].DistinguishedName;
            var foundSam = res.Entries[0].Attributes.Contains("sAMAccountName")
                ? res.Entries[0].Attributes["sAMAccountName"][0]?.ToString()
                : "(none)";
            var foundUpn = res.Entries[0].Attributes.Contains("userPrincipalName")
                ? res.Entries[0].Attributes["userPrincipalName"][0]?.ToString()
                : "(none)";

            // Basic breadcrumb for troubleshooting
            var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("LdapUserSearch");
            logger.LogInformation("FindUserDn: {Count} entries in {Ms} ms; picked DN='{Dn}', sAM='{Sam}', UPN='{Upn}'",
                res.Entries.Count, sw.ElapsedMilliseconds, dn, foundSam, foundUpn);

            return dn;
        }

        private static async Task<bool> IsUserMemberOfGroupDnAsync(LdapConnection conn, string baseDn, string userDn, string groupDn, CancellationToken ct)
        {
            // LDAP_MATCHING_RULE_IN_CHAIN for nested membership
            string filter = $"(&(distinguishedName={Escape(groupDn)})(member:1.2.840.113556.1.4.1941:={Escape(userDn)}))";
            var req = new SearchRequest(
                baseDn,
                filter,
                SearchScope.Subtree,
                new[] { "distinguishedName" }
            );

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, req,
                PartialResultProcessing.NoPartialResultSupport, null);
            sw.Stop();

            var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("LdapGroupDnCheck");
            logger.LogInformation("GroupDnCheck: filter='{Filter}' → entries={Count} in {Ms} ms",
                filter, res.Entries.Count, sw.ElapsedMilliseconds);

            return res.Entries.Count > 0;
        }

        private static async Task<bool> IsUserMemberOfGroupSamAsync(LdapConnection conn, string baseDn, string userDn, string groupSam, CancellationToken ct)
        {
            // First resolve the group's DN by its sAMAccountName
            string grpFilter = $"(&(objectClass=group)(sAMAccountName={Escape(groupSam)}))";
            var grpReq = new SearchRequest(
                baseDn,
                grpFilter,
                SearchScope.Subtree,
                new[] { "distinguishedName" }
            );

            var sw1 = System.Diagnostics.Stopwatch.StartNew();
            var grpRes = (SearchResponse)await Task.Factory.FromAsync(
                conn.BeginSendRequest, conn.EndSendRequest, grpReq,
                PartialResultProcessing.NoPartialResultSupport, null);
            sw1.Stop();

            var logger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("LdapGroupSamLookup");
            logger.LogInformation("GroupSamLookup: filter='{Filter}' → entries={Count} in {Ms} ms",
                grpFilter, grpRes.Entries.Count, sw1.ElapsedMilliseconds);

            if (grpRes.Entries.Count == 0)
            {
                logger.LogWarning("GroupSamLookup: group with sAM '{Sam}' NOT found.", groupSam);
                return false;
            }

            string groupDn = grpRes.Entries[0].DistinguishedName;
            logger.LogInformation("GroupSamLookup: sAM '{Sam}' resolved to DN '{Dn}'", groupSam, groupDn);

            // Then do the DN membership check (nested)
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var result = await IsUserMemberOfGroupDnAsync(conn, baseDn, userDn, groupDn, ct);
            sw2.Stop();

            logger.LogInformation("GroupSamLookup: membership result={Result} (DN check {Ms} ms)", result, sw2.ElapsedMilliseconds);
            return result;
        }

        // Minimal DN filter escaping for LDAP queries
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
