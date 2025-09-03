using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using KPIMonitor.Models;
using KPIMonitor.Services.Auth;
using Microsoft.Extensions.Logging;

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
            // If already authenticated, skip the login view
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
            _log.LogInformation("Login POST received for user '{User}'", vm?.Username);

            if (!ModelState.IsValid)
            {
                _log.LogWarning("ModelState invalid.");
                return View(vm);
            }

            try
            {
                var normalizedUser = await _ad.ValidateAsync(vm!.Username?.Trim() ?? "", vm.Password ?? "");
                if (normalizedUser is null)
                {
                    _log.LogWarning("AD validation failed for '{User}'.", vm.Username);
                    ModelState.AddModelError("", "Invalid username or password.");
                    return View(vm);
                }

                _log.LogInformation("AD validation success for '{User}'. Issuing cookie.", normalizedUser);

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, normalizedUser),
                    new Claim("ad_user", vm.Username ?? "")
                };
                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction("Index", "Home"); // 🔁 Dashboard

            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unhandled exception during login.");
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
        public IActionResult AccessDenied()
        {
            Response.StatusCode = StatusCodes.Status403Forbidden; // return a real 403
            return View();
        }
    }
}
