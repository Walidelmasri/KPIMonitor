using Oracle.ManagedDataAccess.Client;
using KPIMonitor.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using KPIMonitor.Services.Auth;
using KPIMonitor.Services;
using KPIMonitor.Services.Abstractions;
using System.Globalization;
using Microsoft.AspNetCore.Localization;

// alias to avoid clash with KPIMonitor.Services.StatusCodes
using HttpStatusCodes = Microsoft.AspNetCore.Http.StatusCodes;

var builder = WebApplication.CreateBuilder(args);

// DB
string oracleConnStr = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContext<AppDbContext>(options => options.UseOracle(oracleConnStr));

builder.Services.AddHttpContextAccessor();

builder.Services.AddLocalization(options => options.ResourcesPath = "Resources");

builder.Services.AddControllersWithViews(o =>
{
    o.Conventions.Add(new ProtectCreateActionsConvention());
})
.AddViewLocalization()
.AddDataAnnotationsLocalization();

// DI
builder.Services.AddScoped<IEmployeeDirectory, OracleEmployeeDirectory>();
builder.Services.AddScoped<IKpiYearPlanOwnerEditorService, KpiYearPlanOwnerEditorService>();
builder.Services.AddScoped<IKpiAccessService, KpiAccessService>();
builder.Services.AddScoped<IKpiFactChangeService, KpiFactChangeService>();
builder.Services.AddScoped<IStrategyMapService, StrategyMapService>();
builder.Services.AddScoped<IPriorityMatrixService, PriorityMatrixService>();
builder.Services.Configure<AdminOptions>(builder.Configuration.GetSection("App"));
builder.Services.AddScoped<IAdminAuthorizer, ConfigAdminAuthorizer>();
builder.Services.AddScoped<IKpiFactChangeBatchService, KpiFactChangeBatchService>();
builder.Services.AddScoped<IKpiStatusService, KpiStatusService>();
builder.Services.AddScoped<IEmailSender, EmailSenderService>();
builder.Services.AddScoped<IAdAuthenticator, LdapAdAuthenticator>();
builder.Services.AddSingleton<TargetEditLockState>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        //Remove dev for prod server
        o.Cookie.Name = "KpiMonitorAuthDev";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        // o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/AccessDenied";
        // o.SlidingExpiration = false;
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    });

// Global requirement: must be authenticated + in Steervision
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        // .RequireClaim("ad:inSteervision", "true")
        .Build();

    options.AddPolicy("AdminOnly", policy =>
        policy.Requirements.Add(new AdminOnlyRequirement()));
});
builder.Services.AddScoped<IAuthorizationHandler, AdminOnlyHandler>();

var englishCulture = new CultureInfo("en");

var arabicCulture = new CultureInfo("ar");
arabicCulture.DateTimeFormat.Calendar = new GregorianCalendar();
arabicCulture.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
arabicCulture.DateTimeFormat.LongDatePattern = "dd/MM/yyyy";
arabicCulture.DateTimeFormat.FullDateTimePattern = "dd/MM/yyyy HH:mm:ss";

var supportedCultures = new[]
{
    englishCulture,
    arabicCulture
};

builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(englishCulture);
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// app.UseHttpsRedirection();
app.UseStaticFiles();

var locOptions = app.Services
    .GetRequiredService<Microsoft.Extensions.Options.IOptions<RequestLocalizationOptions>>()
    .Value;

app.UseRequestLocalization(locOptions);

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

// 403 DEBUG logger (does not interfere with pipeline)
app.Use(async (ctx, next) =>
{
    await next();
    if (ctx.Response.StatusCode == HttpStatusCodes.Status403Forbidden)
    {
        var lf = ctx.RequestServices.GetRequiredService<ILoggerFactory>();
        var log = lf.CreateLogger("AuthDebug");
        var user = ctx.User?.Identity?.Name ?? "(anonymous)";
        var claims = string.Join(", ", ctx.User?.Claims?.Select(c => $"{c.Type}={c.Value}") ?? Array.Empty<string>());
        log.LogWarning("403 for {Path} user={User} claims=[{Claims}] returnUrl={ReturnUrl}",
            ctx.Request.Path, user, claims, ctx.Request.Query["ReturnUrl"].ToString());
    }
});

// For edit windows (existing)
PeriodEditPolicy.Configure(app.Services.GetRequiredService<IAdminAuthorizer>());

app.MapGet("/culture/set", (string culture, string? returnUrl, HttpContext httpContext) =>
{
    if (culture != "en" && culture != "ar")
        culture = "en";

    var cookieValue = CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture));
    var pathBase = httpContext.Request.PathBase.HasValue
        ? httpContext.Request.PathBase.Value!
        : "/";

    var commonOptions = new CookieOptions
    {
        Expires = DateTimeOffset.UtcNow.AddYears(1),
        IsEssential = true,
        HttpOnly = false,
        Secure = httpContext.Request.IsHttps,
        SameSite = SameSiteMode.Lax
    };

    // Always write root cookie too, to overwrite any old stale one
    httpContext.Response.Cookies.Append(
        CookieRequestCultureProvider.DefaultCookieName,
        cookieValue,
        new CookieOptions
        {
            Expires = commonOptions.Expires,
            IsEssential = commonOptions.IsEssential,
            HttpOnly = commonOptions.HttpOnly,
            Secure = commonOptions.Secure,
            SameSite = commonOptions.SameSite,
            Path = "/"
        });

    // If running under a virtual directory, also write path-based cookie
    if (!string.Equals(pathBase, "/", StringComparison.Ordinal))
    {
        httpContext.Response.Cookies.Append(
            CookieRequestCultureProvider.DefaultCookieName,
            cookieValue,
            new CookieOptions
            {
                Expires = commonOptions.Expires,
                IsEssential = commonOptions.IsEssential,
                HttpOnly = commonOptions.HttpOnly,
                Secure = commonOptions.Secure,
                SameSite = commonOptions.SameSite,
                Path = pathBase
            });
    }

    if (!string.IsNullOrWhiteSpace(returnUrl) && Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        return Results.LocalRedirect(returnUrl);

    return Results.LocalRedirect("/");
})
.AllowAnonymous();

// Default route to login (IIS virtual dir handled by PathBase automatically)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

await app.Services.GetRequiredService<TargetEditLockState>().WarmUpAsync(CancellationToken.None);

app.Run();