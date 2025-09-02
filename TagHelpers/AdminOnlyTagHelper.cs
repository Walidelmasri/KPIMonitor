using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Razor.TagHelpers;

[HtmlTargetElement(Attributes = "asp-admin-only")]
public sealed class AdminOnlyTagHelper : TagHelper
{
    private readonly IHttpContextAccessor _http;
    private readonly IAdminAuthorizer _auth;

    public AdminOnlyTagHelper(IHttpContextAccessor http, IAdminAuthorizer auth)
    {
        _http = http; _auth = auth;
    }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var user = _http.HttpContext?.User;
        if (user is null || !_auth.IsAdmin(user))
            output.SuppressOutput(); // element simply won’t render
    }
}