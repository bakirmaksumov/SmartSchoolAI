using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Services;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────────────────────────

builder.Services.AddControllersWithViews();

// Database — SQLite (swap UseSqlite for UseNpgsql to migrate to PostgreSQL later)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")));

// Domain services
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<PdfImportService>();
builder.Services.AddScoped<HandwritingAnalysisService>();
builder.Services.AddScoped<AnswerGradingService>();
builder.Services.AddScoped<TranslationService>();
builder.Services.AddScoped<GradingService>();
builder.Services.AddScoped<AnalyticsService>();
builder.Services.AddScoped<ClassStatisticsService>();
builder.Services.AddScoped<ReCaptchaService>();
builder.Services.AddSingleton<GenerationJobTracker>();
builder.Services.AddHttpClient();

// Cookie-based authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath          = "/Auth/Login";
        options.LogoutPath         = "/Auth/Logout";
        options.AccessDeniedPath   = "/Auth/Login";
        options.ExpireTimeSpan     = TimeSpan.FromHours(8);
        options.Cookie.HttpOnly    = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization();

// ChatStream posts a JSON body (not a form), so the antiforgery token has to
// travel as a header instead of the usual hidden form field — this just names
// which header [ValidateAntiForgeryToken] should look for.
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");

// Rate limiting — protects the login form from brute-forcing and the
// paid AI-calling endpoints (PDF/image import, grading, tutor chat) from abuse.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("login", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPermitLimit", 5),
            Window      = TimeSpan.FromMinutes(1),
            QueueLimit  = 0
        }));

    options.AddPolicy("ai", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.User.Identity?.Name
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 15,
            Window      = TimeSpan.FromMinutes(5),
            QueueLimit  = 0
        }));
});

// ── Pipeline ──────────────────────────────────────────────────────────────────

var app = builder.Build();

// The site runs behind a reverse proxy that terminates TLS and forwards plain
// HTTP to Kestrel. Without this, the app thinks every request is HTTP — so the
// login-challenge redirect (and anything else that builds an absolute URL) came
// back as http://…, silently downgrading users off HTTPS on every sign-in.
//
// ForwardedHeadersOptions only trusts X-Forwarded-* from KnownProxies/KnownNetworks,
// which default to loopback (127.0.0.1/::1) only — that's what still broke this in
// production, because the proxy isn't on loopback relative to this process. Clearing
// both lists makes the middleware trust whoever connects directly to Kestrel instead.
// That is only safe because Kestrel itself is never exposed to the internet — only
// the reverse proxy is; if that ever changes (Kestrel bound to a public interface,
// or reachable without going through the proxy), this must be scoped back down to
// the proxy's actual address, or a spoofed X-Forwarded-Proto could bypass HTTPS checks.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

if (!app.Environment.IsDevelopment())
{
    // Belt and braces: production still saw http:// redirects even after trusting
    // ForwardedHeaders above, which means the proxy in front of this app isn't
    // sending X-Forwarded-Proto at all — so there is nothing for that middleware
    // to read. Since Kestrel is never reached except through that (always-HTTPS)
    // proxy, it's safe to just declare every request HTTPS outright instead of
    // depending on a header the proxy may or may not send.
    app.Use((context, next) =>
    {
        context.Request.Scheme = "https";
        return next();
    });

    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

// Baseline security headers — mitigate clickjacking, MIME-sniffing, and
// give a first line of defense against XSS even though views already encode output.
//
// connect-src additionally allows ws(s)://localhost in Development only — that's
// Visual Studio's Browser Link and dotnet-watch hot-reload channel, both of which
// open their own localhost websocket outside 'self' and are never present outside
// a local dev run.
//
// https://cdn.jsdelivr.net is required here too, not just in script-src: MathLive
// (the <math-field> editor) fetches its own sound effects and font-metrics data
// with `fetch()` at runtime rather than bundling them inline, and connect-src is
// the directive that governs fetch/XHR targets — script-src only covers the
// initial <script> tag load. Without it typing into a math-field silently does
// nothing (the keystroke handler depends on data it couldn't fetch).
//
// blob: is required for the same reason on a different library: Cropper.js
// (photo crop before OCR) re-reads the picked photo's own `blob:` object URL
// via fetch() to build its crop canvas. img-src already allows blob: — that
// only covers the <img> tag showing the photo, not Cropper's own internal
// fetch of that same URL, which connect-src governs.
var connectSrc = app.Environment.IsDevelopment()
    ? "connect-src 'self' ws://localhost:* wss://localhost:* http://localhost:* https://cdn.jsdelivr.net blob:; "
    : "connect-src 'self' https://cdn.jsdelivr.net blob:; ";

var cspHeaderValue =
    "default-src 'self'; " +
    // 'unsafe-eval' is required by the Bootstrap/theme UMD bundles, which use
    // Function-based environment detection — without it their global objects
    // (e.g. window.bootstrap) never initialize and dependent UI silently breaks.
    "script-src 'self' 'unsafe-inline' 'unsafe-eval' https://cdn.jsdelivr.net https://www.google.com/recaptcha/ https://www.gstatic.com/recaptcha/; " +
    "style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
    "img-src 'self' data: blob:; " +
    "font-src 'self' data: https://cdn.jsdelivr.net https://fonts.gstatic.com; " +
    connectSrc +
    // 'self' is required alongside recaptcha.google.com: the "create question from
    // scratch" modal on /Assignments/Edit embeds /ContentLibrary/Create in an
    // <iframe> to reuse its existing form rather than duplicate it. frame-src is
    // stated explicitly here, so it does NOT fall back to default-src 'self' the
    // way an unset directive would — without listing 'self' too, the browser
    // refuses to let this page frame even its own same-origin content, and the
    // embed renders as a blocked/blank pane ("This content is blocked").
    "frame-src 'self' https://www.google.com/recaptcha/; " +
    // 'self', not 'none': this is the mirror rule, on the framed page's own
    // response — /ContentLibrary/Create must allow a same-origin parent to frame
    // IT, which 'none' would also refuse. 'self' still blocks every other site
    // from framing this app (the actual clickjacking defense) while allowing this
    // one legitimate same-origin use.
    "frame-ancestors 'self'";

app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    // SAMEORIGIN mirrors the CSP frame-ancestors 'self' above for browsers that
    // only understand the older header — see the comment on frame-ancestors.
    context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
    context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.Append("Content-Security-Policy", cspHeaderValue);
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();   // must come before UseAuthorization
app.UseAuthorization();

// Hold users flagged for a password reset on the change-password page. Runs after
// auth so it can read the "must change password" claim from the signed-in user.
app.UseMiddleware<SmartSchoolAI.Middleware.ForcePasswordChangeMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Readiness probe for containers and CI: 200 once the app is serving (startup migrations
// below finish before Kestrel starts listening) and can reach its database, 503 otherwise.
// Anonymous, returns no data, and does no heavy work.
app.MapGet("/health/ready", async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var canConnect = false;
    try { canConnect = await db.Database.CanConnectAsync(cancellationToken); }
    catch (Exception) { /* reported as not ready below */ }

    return canConnect
        ? Results.Ok(new { status = "ready" })
        : Results.Json(new { status = "database unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

// ── Auto-migrate on startup ───────────────────────────────────────────────────
// A bad migration or a crash mid-Migrate() would otherwise leave no way back —
// take a timestamped copy of the SQLite file first, but only when there's
// actually a schema change about to run (not on every ordinary restart).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    if ((await db.Database.GetPendingMigrationsAsync()).Any())
        BackupSqliteDatabase(db, app.Environment);

    db.Database.Migrate();
}

static void BackupSqliteDatabase(AppDbContext db, IWebHostEnvironment env)
{
    var connectionString = db.Database.GetConnectionString();
    var dataSource = connectionString?
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => part.Split('=', 2))
        .FirstOrDefault(kv => kv.Length == 2 && kv[0].Equals("Data Source", StringComparison.OrdinalIgnoreCase))
        ?[1];

    if (string.IsNullOrEmpty(dataSource))
        return;

    var dbPath = Path.IsPathRooted(dataSource) ? dataSource : Path.Combine(env.ContentRootPath, dataSource);
    if (!File.Exists(dbPath))
        return;

    var backupDir = Path.Combine(env.ContentRootPath, "App_Data", "backups");
    Directory.CreateDirectory(backupDir);

    var backupPath = Path.Combine(backupDir, $"{Path.GetFileNameWithoutExtension(dbPath)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.db");
    File.Copy(dbPath, backupPath);
}

// Submission photos and avatars written before they were protected still sit under
// the web root, where anyone could open them by URL. Move them into protected storage.
SubmissionStorage.MigratePubliclyStoredFiles(app.Environment);
AvatarStorage.MigratePubliclyStoredFiles(app.Environment);

app.Run();
