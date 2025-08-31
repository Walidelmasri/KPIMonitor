using Oracle.ManagedDataAccess.Client; // existing
using KPIMonitor.Data;
using Microsoft.EntityFrameworkCore;
using Oracle.EntityFrameworkCore;

// 🔽 NEW usings for cookie auth + DI
using Microsoft.AspNetCore.Authentication.Cookies;
using KPIMonitor.Services.Auth;

var builder = WebApplication.CreateBuilder(args);

// Read connection string from appsettings.json
string oracleConnStr = builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseOracle(oracleConnStr));

builder.Services.AddControllersWithViews();

// 🔽 NEW: Cookie auth for sign-in/sign-out flow
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

// 🔽 NEW: AD authenticator (your LDAP implementation)
builder.Services.AddScoped<IAdAuthenticator, LdapAdAuthenticator>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// 🔽 RECOMMENDED: serve wwwroot (logo/css/js)
app.UseStaticFiles();

app.UseRouting();

// 🔽 NEW: must come before Authorization
app.UseAuthentication();

app.UseAuthorization();

// If you’re using MapStaticAssets()/WithStaticAssets() from a specific package,
// you can keep them, but with MVC the usual is UseStaticFiles() above.
// Remove these two lines if you don’t need them:
// app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}")
    // .WithStaticAssets() // remove if not needed
    ;

app.Run();
