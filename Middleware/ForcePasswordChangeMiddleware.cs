using SmartSchoolAI.Controllers;

namespace SmartSchoolAI.Middleware;

/// <summary>
/// Keeps a signed-in user who still owes a password change on the change-password
/// page. A teacher can reset a student's password to a temporary one and flag the
/// account (<c>User.MustChangePassword</c>); on their next sign-in the login carries
/// that as a cookie claim, and this guard redirects every other request back to
/// <c>/Auth/ChangePassword</c> until they set their own — so a redirect at login
/// alone can't simply be navigated around.
///
/// Static files are served earlier in the pipeline and never reach this middleware,
/// so only the change-password and logout endpoints need to be let through.
/// </summary>
public class ForcePasswordChangeMiddleware
{
    private readonly RequestDelegate _next;

    public ForcePasswordChangeMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var user = context.User;
        if (user.Identity?.IsAuthenticated == true &&
            user.HasClaim(AuthController.MustChangePasswordClaim, "true"))
        {
            var path = context.Request.Path;
            var allowed = path.StartsWithSegments("/Auth/ChangePassword")
                       || path.StartsWithSegments("/Auth/Logout");

            if (!allowed)
            {
                context.Response.Redirect("/Auth/ChangePassword");
                return;
            }
        }

        await _next(context);
    }
}
