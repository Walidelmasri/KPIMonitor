using Oracle.ManagedDataAccess.Client; // existing
using KPIMonitor.Data;
using Microsoft.EntityFrameworkCore;
using Oracle.EntityFrameworkCore;

using Microsoft.AspNetCore.Authentication.Cookies;
using KPIMonitor.Services.Auth;
using KPIMonitor.Services;

var builder = WebApplication.CreateBuilder(args);

// Read connection string from appsettings.json
string oracleConnStr = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseOracle(oracleConnStr));

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<IEmployeeDirectory, OracleEmployeeDirectory>();
builder.Services.AddScoped<IKpiYearPlanOwnerEditorService, KpiYearPlanOwnerEditorService>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "KpiMonitorAuth";   // easy to find/delete
        o.LoginPath = "/Account/Login";
        o.LogoutPath = "/Account/Logout";
        o.AccessDeniedPath = "/Account/Login";
        o.SlidingExpiration = true;
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
    });

builder.Services.AddAuthorization();

builder.Services.AddScoped<IAdAuthenticator, LdapAdAuthenticator>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();

app.UseAuthorization();



app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    // .WithStaticAssets() 
    ;

app.Run();
