using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Services;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Shows a teacher how their class is doing. The counting lives in <see cref="AnalyticsService"/>; this controller
/// only reads the address. Every filter, tab and drill-down is part of the address, so Back returns to the same view.
/// </summary>
[Authorize(Roles = "Teacher")]
public class AnalyticsController : Controller
{
    private readonly AnalyticsService _analytics;

    public AnalyticsController(AnalyticsService analytics) => _analytics = analytics;

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET: /Analytics?classId=…&tab=overview|assignments|students|questions|work&type=all|practice|homework
    //                 &period=all|7d|30d|90d&status=…&assignmentId=…&studentId=…&questionAssignment=…
    // Old links with tab=practice|assessment open the overview filtered to that type.
    public async Task<IActionResult> Index(
        Guid? classId = null, string? tab = null, string? type = null, string? period = null,
        string? status = null, Guid? assignmentId = null, Guid? studentId = null, Guid? questionAssignment = null)
    {
        var model = await _analytics.BuildAsync(CurrentUserId,
            new AnalyticsService.Request(classId, tab, type, period, status, assignmentId, studentId, questionAssignment));
        return model is null ? NotFound() : View(model);
    }
}
