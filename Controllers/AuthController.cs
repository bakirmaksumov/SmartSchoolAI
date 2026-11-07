using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Handles authentication: Login, Register, and Logout.
/// All business logic is delegated to AuthService — this controller is intentionally thin.
/// </summary>
public class AuthController : Controller
{
    private readonly AuthService _authService;
    private readonly ReCaptchaService _reCaptchaService;
    private readonly IWebHostEnvironment _env;

    public AuthController(AuthService authService, ReCaptchaService reCaptchaService, IWebHostEnvironment env)
    {
        _authService = authService;
        _reCaptchaService = reCaptchaService;
        _env = env;
    }

    // ── LOGIN ──────────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Login()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        ViewBag.ReCaptchaSiteKey = _reCaptchaService.SiteKey;
        return View();
    }

    // Neither role has a self-service reset flow — a student's teacher can
    // reset it for them (ClassesController.ResetStudentPassword), and a
    // teacher's school administrator can (AdminController.ResetTeacherPassword).
    // This just points people at whichever of those applies, instead of the
    // link 404ing.
    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(
        string email, string password, bool rememberMe, [Bind(Prefix = "g-recaptcha-response")] string? reCaptchaToken)
    {
        ViewBag.ReCaptchaSiteKey = _reCaptchaService.SiteKey;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            ViewBag.Error = "Please enter your email and password.";
            return View();
        }

        if (!await _reCaptchaService.VerifyAsync(reCaptchaToken, HttpContext.Connection.RemoteIpAddress?.ToString()))
        {
            ViewBag.Error = "Please confirm you're not a robot.";
            return View();
        }

        var user = await _authService.ValidateAsync(email, password);

        if (user is null)
        {
            ViewBag.Error = "Invalid email or password.";
            return View();
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name,           user.FullName),
            new(ClaimTypes.Email,          user.Email),
            new(ClaimTypes.Role,           user.Role.Name),
        };

        // Carry the "must change password" state in the cookie so the guard middleware
        // can hold the user on the change-password page until they pick a new one.
        if (user.MustChangePassword)
            claims.Add(new Claim(MustChangePasswordClaim, "true"));

        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = rememberMe,
                ExpiresUtc   = rememberMe
                    ? DateTimeOffset.UtcNow.AddDays(30)
                    : DateTimeOffset.UtcNow.AddHours(8)
            });

        ActivityLog.Record(_env, "Login", user.Role.Name, user.Id);

        if (user.MustChangePassword)
            return RedirectToAction(nameof(ChangePassword));

        if (user.Role.Name == Role.Student)
            return RedirectToAction("Index", "Student");
        else
            return RedirectToAction("Index", "Home");
    }

    // ── FORCED PASSWORD CHANGE ───────────────────────────────────────────────────

    /// <summary>
    /// Claim that marks a signed-in user as still owing a password change. The
    /// <see cref="Middleware.ForcePasswordChangeMiddleware"/> keeps such users on
    /// <see cref="ChangePassword"/> until they set one; it is dropped from the cookie
    /// once they do.
    /// </summary>
    public const string MustChangePasswordClaim = "must_change_password";

    [HttpGet]
    [Authorize]
    public IActionResult ChangePassword() => View();

    [HttpPost]
    [Authorize]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(string newPassword, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            ViewBag.Error = "Password must be at least 8 characters.";
            return View();
        }

        if (newPassword != confirmPassword)
        {
            ViewBag.Error = "Passwords do not match.";
            return View();
        }

        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        if (!await _authService.ChangePasswordAsync(userId, newPassword))
        {
            ViewBag.Error = "Something went wrong. Please sign in again.";
            return View();
        }

        // Re-issue the cookie without the must-change claim so the guard lets them
        // through. Everything else about their identity stays exactly as it was —
        // including whether "Remember Me" was checked at login: without carrying
        // the original AuthenticationProperties forward, SignInAsync defaults to
        // a non-persistent session cookie, silently downgrading a 30-day
        // "remembered" login to one that expires when the browser closes.
        var fresh = new ClaimsIdentity(
            User.Claims.Where(c => c.Type != MustChangePasswordClaim),
            CookieAuthenticationDefaults.AuthenticationScheme);
        var currentTicket = await HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(fresh), currentTicket.Properties);

        TempData["Success"] = "Your password has been updated.";
        return User.IsInRole(Role.Student)
            ? RedirectToAction("Index", "Student")
            : RedirectToAction("Index", "Home");
    }

    // ── REGISTER ───────────────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult Register()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");

        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Register(
        string email, string fullName, string password, string confirmPassword)
    {
        if (string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(fullName) ||
            string.IsNullOrWhiteSpace(password))
        {
            ViewBag.Error = "All fields are required.";
            return View();
        }

        if (password != confirmPassword)
        {
            ViewBag.Error = "Passwords do not match.";
            return View();
        }

        if (password.Length < 8)
        {
            ViewBag.Error = "Password must be at least 8 characters.";
            return View();
        }

        var (success, error) = await _authService.RegisterTeacherAsync(email, fullName, password);

        if (!success)
        {
            ViewBag.Error = error;
            return View();
        }

        TempData["Success"] = "Account created! Please sign in.";
        return RedirectToAction(nameof(Login));
    }

    // ── STUDENT REGISTER ───────────────────────────────────────────────────────

    [HttpGet]
    public IActionResult StudentRegister()
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Index", "Home");
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> StudentRegister(
        string email, string fullName, string password, string confirmPassword, string inviteCode)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(fullName) ||
            string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(inviteCode))
        {
            ViewBag.Error = "All fields are required.";
            return View();
        }

        if (password != confirmPassword)
        {
            ViewBag.Error = "Passwords do not match.";
            return View();
        }

        if (password.Length < 8)
        {
            ViewBag.Error = "Password must be at least 8 characters.";
            return View();
        }

        var (success, error) = await _authService.RegisterStudentAsync(email, fullName, password, inviteCode);

        if (!success)
        {
            ViewBag.Error = error;
            return View();
        }

        TempData["Success"] = "Account created! Please sign in.";
        return RedirectToAction(nameof(Login));
    }

    // ── LOGOUT ─────────────────────────────────────────────────────────────────

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }
}
