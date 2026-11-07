using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Services;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Shows a teacher how their class is doing. The aggregation lives in
/// <see cref="AnalyticsService"/>; this controller only handles the request.
/// </summary>
[Authorize]
public class AnalyticsController : Controller
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics) => _analytics = analytics;

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET: /Analytics?classId=…&tab=practice|assessment
    public async Task<IActionResult> Index(Guid? classId = null, string tab = "practice")
        => View(await _analytics.BuildAsync(CurrentUserId, classId, tab));
}
