using Microsoft.AspNetCore.Authorization;

public sealed class AdminOnlyHandler : AuthorizationHandler<AdminOnlyRequirement>
{
    private readonly IAdminAuthorizer _auth;
    public AdminOnlyHandler(IAdminAuthorizer auth) => _auth = auth;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminOnlyRequirement requirement)
    {
        if (_auth.IsAdmin(context.User))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
