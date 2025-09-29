using System.Security.Claims;
public interface IAdminAuthorizer
{
    bool IsAdmin(ClaimsPrincipal user);
    bool IsSuperAdmin(ClaimsPrincipal user);

}