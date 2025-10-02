using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using KPIMonitor.Models;
using KPIMonitor.Services.Auth;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KPIMonitor.Controllers
{
    public class AccountController : Controller
    {
        private readonly IAdAuthenticator _ad;
        private readonly ILogger<AccountController> _log;

        public AccountController(IAdAuthenticator ad, ILogger<AccountController> log)
        {
            _ad = ad;
            _log = log;
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction("Index", "Home");
            }

            ViewBag.ReturnUrl = returnUrl;
            return View(new LoginViewModel());
        }

        [HttpPost]
        [AllowAnonymous]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Login(LoginViewModel vm, string? returnUrl = null)
        {
            _log.LogInformation("Login POST for user '{User}'", vm?.Username);

            if (!ModelState.IsValid)
            {
                _log.LogWarning("ModelState invalid.");
                return View(vm);
            }

            try
            {
                var inputUser = vm!.Username?.Trim() ?? "";
                var pwd = vm.Password ?? "";

                // 1) Validate credentials -> returns NETBIOS\sam (e.g., "BADEA\\jdoe")
                var normalizedUser = await _ad.ValidateAsync(inputUser, pwd);
                if (normalizedUser is null)
                {
                    _log.LogWarning("AD validation failed for '{User}'.", vm.Username);
                    ModelState.AddModelError("", "Invalid username or password.");
                    return View(vm);
                }

                _log.LogInformation("Login VALIDATED. normalizedUser={NormalizedUser}", normalizedUser);

                // 2) ISSUE COOKIE FIRST (as before)
                var baseClaims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, normalizedUser), // EXACT old format (BADEA\sam)
                    new Claim("ad_user", inputUser)
                };
                var baseIdentity = new ClaimsIdentity(baseClaims, CookieAuthenticationDefaults.AuthenticationScheme);
                var basePrincipal = new ClaimsPrincipal(baseIdentity);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, basePrincipal);

                // 3) AFTER sign-in: check Steervision membership
                _log.LogInformation("Post-login GROUP CHECK for user={User}", normalizedUser);

                var inGroup = await _ad.IsMemberOfAllowedGroupAsync(normalizedUser, pwd);
                if (!inGroup)
                {
                    _log.LogWarning("User '{User}' NOT in allowed AD group. Keeping cookie for debugging.", normalizedUser);

                    // Keep the cookie but add a claim marking the failure so AccessDenied shows you as authenticated
                    var debugClaims = new List<Claim> { new Claim("ad:inSteervision", "false") };
                    var debugIdentity = new ClaimsIdentity(debugClaims, CookieAuthenticationDefaults.AuthenticationScheme);

                    var debugPrincipal = new ClaimsPrincipal(new[]
                    {
                        baseIdentity,    // what we already issued
                        debugIdentity    // plus the "inSteervision=false" flag
                    });

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, debugPrincipal);

                    var reason = "Not in Steervision AD group.";
                    return RedirectToAction(nameof(AccessDenied), new { reason, who = normalizedUser, returnUrl });
                }

                // 4) In group → reissue cookie with the positive claim
                var groupClaims = new List<Claim> { new Claim("ad:inSteervision", "true") };
                var groupIdentity = new ClaimsIdentity(groupClaims, CookieAuthenticationDefaults.AuthenticationScheme);
                var finalPrincipal = new ClaimsPrincipal(new[] { baseIdentity, groupIdentity });
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, finalPrincipal);

                // 5) Normal redirect
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction("Index", "Home");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception during login for user '{User}'.", vm?.Username);
                ModelState.AddModelError("", $"Login error: {ex.Message}");
                return View(vm);
            }
        }

        [Authorize]
        [HttpGet]
        public IActionResult AdOk()
        {
            var who = User.Identity?.Name ?? "unknown";
            return Content($"🎉 Congrats — AD is working. You are: {who}", "text/plain");
        }

        [Authorize]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return RedirectToAction(nameof(Login));
        }

        [AllowAnonymous]
        public IActionResult AccessDenied(string? reason = null, string? who = null, string? returnUrl = null, string? format = null)
        {
            Response.StatusCode = 403;

            var vm = new KPIMonitor.Models.AuthDebugVm
            {
                Reason = reason ?? "Access denied: you are not authorized for this application.",
                UserName = who ?? (User?.Identity?.Name ?? "(unknown)"),
                IsAuthenticated = User?.Identity?.IsAuthenticated == true,
                RequestUrl = HttpContext?.Request?.Path.Value + HttpContext?.Request?.QueryString.Value,
                ReturnUrl = returnUrl ?? "",
                Method = Request?.Method ?? "",
                RemoteIp = HttpContext?.Connection?.RemoteIpAddress?.ToString() ?? "",
                HasSteervisionClaim = User?.Claims?.Any(c => c.Type == "ad:inSteervision") == true,
                SteervisionClaimValue = User?.Claims?.FirstOrDefault(c => c.Type == "ad:inSteervision")?.Value ?? "",
                Claims = (User?.Claims ?? Enumerable.Empty<System.Security.Claims.Claim>())
                         .Select(c => new KPIMonitor.Models.AuthDebugClaim { Type = c.Type, Value = c.Value })
                         .ToList(),
                Headers = new Dictionary<string, string>
                {
                    ["Referer"] = Request.Headers["Referer"].ToString(),
                    ["User-Agent"] = Request.Headers["User-Agent"].ToString()
                },
                Cookies = Request.Cookies.Keys.ToList()
            };

            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
                Request.Headers["Accept"].ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return Json(vm);
            }

            return View(vm);
        }
    }
}
