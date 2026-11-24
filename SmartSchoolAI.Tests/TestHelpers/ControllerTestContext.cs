using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Primitives;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>Builds the ControllerContext controllers expect: a signed-in user's claims and, optionally, form data.</summary>
public static class ControllerTestContext
{
    public static void SignIn(
        Controller controller, Guid userId, string email, string role = "Teacher",
        IDictionary<string, string>? form = null,
        IDictionary<string, string[]>? multiForm = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Email,          email),
            new(ClaimTypes.Role,           role),
        };
        var identity  = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { User = principal };

        if (form is not null || multiForm is not null)
        {
            var values = new Dictionary<string, StringValues>();
            if (form is not null)
                foreach (var kv in form) values[kv.Key] = new StringValues(kv.Value);
            // Repeated <input name="..."> fields (table cells, operands, …) post as
            // several values under one key — a plain string can't represent that.
            if (multiForm is not null)
                foreach (var kv in multiForm) values[kv.Key] = new StringValues(kv.Value);
            httpContext.Request.Form = new FormCollection(values);
        }

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        // Controllers write to TempData (e.g. success banners) outside of a real request
        // pipeline, which normally supplies it via DI. No storage is asserted on in tests,
        // so a provider that never actually persists anything is enough to satisfy the call.
        controller.TempData = new TempDataDictionary(httpContext, NoOpTempDataProvider.Instance);
    }

    private sealed class NoOpTempDataProvider : ITempDataProvider
    {
        public static readonly NoOpTempDataProvider Instance = new();

        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
