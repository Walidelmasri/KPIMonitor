using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using KPIMonitor.Models;
using KPIMonitor.Services.Auth;
using Microsoft.Extensions.Logging;
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

                // 2) ISSUE COOKIE FIRST (like before)
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, normalizedUser), // EXACT old format (BADEA\sam)
                    new Claim("ad_user", inputUser)
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));

                // 3) AFTER sign-in: check Steervision membership using the SAME identity
                // Extract sam from "DOMAIN\sam"
                var idx = normalizedUser.IndexOf('\\');
                var sam = idx >= 0 ? normalizedUser[(idx + 1)..] : normalizedUser;

                _log.LogInformation("Post-login GROUP CHECK for sam={Sam}", sam);

                var inGroup = await _ad.IsMemberOfAllowedGroupAsync(normalizedUser, pwd);
                if (!inGroup)
                {
                    _log.LogWarning("User '{User}' NOT in allowed AD group. Signing out.", normalizedUser);

                    // Remove cookie we just set
                    await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

                    // Send them to AccessDenied with a reason and who
                    var reason = "Not in Steervision AD group.";
                    return RedirectToAction(nameof(AccessDenied), new { reason, who = normalizedUser, returnUrl });
                }

                // 4) If in group, optionally stamp a claim for later use
                var addlClaims = new List<Claim> { new Claim("ad:inSteervision", "true") };
                var addlIdentity = new ClaimsIdentity(addlClaims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(new[] { identity, addlIdentity });
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

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
