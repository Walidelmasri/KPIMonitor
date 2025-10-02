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
            _log.LogInformation("Login POST for user '{User}'", vm?.Username);

            if (!ModelState.IsValid)
            {
                _log.LogWarning("ModelState invalid.");
                return View(vm);
            }

            try
            {
                var userInput = vm!.Username?.Trim() ?? "";
                var pwd = vm.Password ?? "";

                // 1) Validate credentials (exact same flow you had)
                var normalizedUser = await _ad.ValidateAsync(userInput, pwd);
                if (normalizedUser is null)
                {
                    _log.LogWarning("AD validation failed for '{User}'.", vm.Username);
                    ModelState.AddModelError("", "Invalid username or password.");
                    return View(vm);
                }

                // 2) Check Steervision membership using the SAME creds
                var inGroup = await _ad.IsMemberOfAllowedGroupAsync(userInput, pwd);
                if (!inGroup)
                {
                    _log.LogWarning("User '{User}' authenticated but not in allowed AD group.", normalizedUser);
                    // Stay on the Login page with a clear error (no cookie issued)
                    ModelState.AddModelError("", "Access denied: you are not authorized for this application.");
                    return View(vm);
                }

                _log.LogInformation("Login success + group OK for '{User}'. Issuing cookie.", normalizedUser);

                // 3) Issue the auth cookie (unchanged behavior)
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, normalizedUser), // e.g., BADEA\jdoe
                    new Claim("ad_user", vm.Username ?? "")
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));

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
        public IActionResult AccessDenied()
        {
            Response.StatusCode = 403; // return a real 403
            return View();
        }
    }
}
