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
                var inputUser = vm!.Username?.Trim() ?? string.Empty;
                var inputPass = vm.Password ?? string.Empty;

                // 1) Credentials check (bind to AD)
                var normalizedUser = await _ad.ValidateAsync(inputUser, inputPass);
                if (normalizedUser is null)
                {
                    _log.LogWarning("AD validation failed for '{User}'.", vm.Username);
                    ModelState.AddModelError("", "Invalid username or password.");
                    return View(vm);
                }

                // 2) Group gate (must be in allowed AD group, e.g., Steervision)
                var inGroup = await _ad.IsMemberOfAllowedGroupAsync(inputUser, inputPass);
                if (!inGroup)
                {
                    _log.LogWarning("User '{User}' authenticated but not in allowed group.", inputUser);
                    // AuthZ pipeline will route to /Account/AccessDenied (configured in Program.cs)
                    return Forbid(CookieAuthenticationDefaults.AuthenticationScheme);
                }

                _log.LogInformation("AD validation + group check success for '{User}'. Issuing cookie.", normalizedUser);

                // 3) Issue auth cookie
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, normalizedUser),     // SAM (lowercase) per authenticator
                    new Claim("ad_user", vm.Username ?? string.Empty),
                    new Claim("ad:inSteervision", "true")           // mark membership for downstream checks
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity));

                // 4) Post-login redirect
                if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
                    return Redirect(returnUrl);

                return RedirectToAction("Index", "Home");
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
        public IActionResult AccessDenied(string? returnUrl = null)
        {
            Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden;

            var r = HttpContext.Request;

            // Build a Request URL
            var fullUrl = $"{r.Scheme}://{r.Host}{r.Path}{r.QueryString}";

            // Pull a few useful headers
            var headers = new[] { "Referer", "User-Agent", "X-Forwarded-For", "X-Original-URL" }
                .Where(h => r.Headers.ContainsKey(h))
                .ToDictionary(h => h, h => r.Headers[h].ToString());

            // Cookie names only (avoid leaking values)
            var cookies = r.Cookies?.Keys?.ToList() ?? new List<string>();

            // Claims
            var claims = User?.Claims?.Select(c => (c.Type, c.Value)).ToList() ?? new List<(string,string)>();
            var steervisionClaim = claims.FirstOrDefault(c => c.Type == "ad:inSteervision").Value;

            // Reason (best-effort explanation)
            string reason;
            if (!(User?.Identity?.IsAuthenticated ?? false))
            {
                reason = "Not authenticated (no auth cookie or expired).";
            }
            else if (string.IsNullOrEmpty(steervisionClaim))
            {
                reason = "Missing required claim 'ad:inSteervision' (fallback policy).";
            }
            else
            {
                reason = "Authenticated but blocked by another policy/authorization rule.";
            }

            var vm = new AuthDebugVm
            {
                StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden,
                UserName = User?.Identity?.Name ?? "(anonymous)",
                IsAuthenticated = User?.Identity?.IsAuthenticated ?? false,
                ReturnUrl = returnUrl ?? r.Query["ReturnUrl"].ToString(),
                RequestUrl = fullUrl,
                Method = r.Method,
                RemoteIp = HttpContext.Connection.RemoteIpAddress?.ToString(),
                Claims = claims,
                HasSteervisionClaim = !string.IsNullOrEmpty(steervisionClaim),
                SteervisionClaimValue = steervisionClaim,
                Reason = reason,
                Headers = headers,
                Cookies = cookies
            };

            // Also log to server logs (Event Viewer if configured)
            _log.LogWarning("AccessDenied user={User} reason={Reason} url={Url} returnUrl={ReturnUrl} claims=[{Claims}]",
                vm.UserName, vm.Reason, vm.RequestUrl, vm.ReturnUrl,
                string.Join("; ", vm.Claims.Select(c => $"{c.Type}={c.Value}")));

            return View(vm);
        }

    }
}
