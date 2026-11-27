using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>The notifications bell: marking what was seen, and the unread count for a page left open.</summary>
[Authorize]
[Route("Notifications")]
public class NotificationsController : Controller
{
    private readonly NotificationService _notifications;

    public NotificationsController(NotificationService notifications) => _notifications = notifications;

    private Guid? UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>The bell was opened: everything up to now is seen.</summary>
    [HttpPost("Seen")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Seen()
    {
        if (UserId is not { } id) return Unauthorized();
        await _notifications.MarkSeenAsync(id);
        return NoContent();
    }

    /// <summary>The bell's menu, rendered when it is opened.</summary>
    [HttpGet("Menu")]
    public async Task<IActionResult> Menu()
    {
        if (UserId is not { } id) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var feed = await _notifications.ForUserAsync(id, User.FindFirstValue(ClaimTypes.Role));
        return PartialView("~/Views/Shared/Components/Notifications/_Menu.cshtml", feed);
    }

    [HttpGet("Count")]
    public async Task<IActionResult> Count()
    {
        if (UserId is not { } id) return Unauthorized();
        Response.Headers.CacheControl = "no-store";
        var feed = await _notifications.ForUserAsync(id, User.FindFirstValue(ClaimTypes.Role));
        return Json(new { unread = feed.Unread });
    }
}
