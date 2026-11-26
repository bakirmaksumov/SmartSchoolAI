namespace SmartSchoolAI.Middleware;

/// <summary>
/// Tells the browser not to keep any page served to a signed-in user. Without it Chrome holds such pages in
/// its back/forward cache: after one person logs out (or another signs in on the same computer), pressing
/// Back showed the previous user's dashboard from memory. With <c>no-store</c> the browser has to ask the
/// server again, which answers for whoever is signed in now, or sends the visitor to the login page.
///
/// Static files (CSS, JS, images) are served earlier in the pipeline, never reach this middleware and stay
/// cacheable. A response that sets its own Cache-Control (the tutor's event stream) keeps it.
/// </summary>
public class NoStoreForSignedInMiddleware
{
    public const string CacheControl = "no-store, no-cache, max-age=0";

    private readonly RequestDelegate _next;

    public NoStoreForSignedInMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            // Set before the action runs, so an action that needs a different value can still overwrite it.
            context.Response.Headers.CacheControl = CacheControl;
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }

        return _next(context);
    }
}
