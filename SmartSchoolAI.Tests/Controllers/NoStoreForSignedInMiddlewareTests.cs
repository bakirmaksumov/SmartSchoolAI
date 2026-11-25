using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using SmartSchoolAI.Middleware;
using Xunit;

namespace SmartSchoolAI.Tests.Controllers;

public class NoStoreForSignedInMiddlewareTests
{
    private static async Task<HttpContext> Run(bool signedIn, RequestDelegate? action = null)
    {
        var context = new DefaultHttpContext();
        if (signedIn)
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "t") }, "Cookies"));
        var middleware = new NoStoreForSignedInMiddleware(action ?? (_ => Task.CompletedTask));
        await middleware.InvokeAsync(context);
        return context;
    }

    [Fact]
    public async Task Signed_in_pages_are_not_kept_by_the_browser()
    {
        var context = await Run(signedIn: true);

        Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal("no-cache", context.Response.Headers.Pragma.ToString());
    }

    [Fact]
    public async Task Anonymous_pages_are_left_alone()
    {
        var context = await Run(signedIn: false);

        Assert.Empty(context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task An_action_can_still_set_its_own_cache_control()
    {
        var context = await Run(signedIn: true, ctx => { ctx.Response.Headers.CacheControl = "no-cache"; return Task.CompletedTask; });

        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());
    }
}
