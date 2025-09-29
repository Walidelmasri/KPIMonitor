using Microsoft.Extensions.Options;
using System.Security.Claims;

public sealed class ConfigAdminAuthorizer : IAdminAuthorizer
{
    private readonly AdminOptions _opt;
    public ConfigAdminAuthorizer(IOptions<AdminOptions> opt) => _opt = opt.Value;

    public bool IsAdmin(ClaimsPrincipal user)
    {
        var name = (user.FindFirst("ad_user")?.Value ?? user.Identity?.Name ?? "").Trim();

        // normalize DOMAIN\user or user@domain to "user"
        var slash = name.LastIndexOf('\\'); if (slash >= 0) name = name[(slash + 1)..];
        var at = name.IndexOf('@'); if (at >= 0) name = name[..at];

        foreach (var admin in _opt.AdminUsers)
        {
            var a = admin.Trim();
            var s = a.LastIndexOf('\\'); if (s >= 0) a = a[(s + 1)..];
            var t = a.IndexOf('@'); if (t >= 0) a = a[..t];
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    // NEW — mirrors IsAdmin logic but checks SuperAdminUsers list instead
    public bool IsSuperAdmin(ClaimsPrincipal user)
    {
        var name = (user.FindFirst("ad_user")?.Value ?? user.Identity?.Name ?? "").Trim();


        // normalize DOMAIN\\user or user@domain to "user"
        var slash = name.LastIndexOf('\\'); if (slash >= 0) name = name[(slash + 1)..];
        var at = name.IndexOf('@'); if (at >= 0) name = name[..at];


        foreach (var admin in _opt.SuperAdminUsers)
        {
            var a = admin.Trim();
            var s = a.LastIndexOf('\\'); if (s >= 0) a = a[(s + 1)..];
            var t = a.IndexOf('@'); if (t >= 0) a = a[..t];
            if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}