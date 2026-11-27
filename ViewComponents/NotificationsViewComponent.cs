using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.ViewComponents;

/// <summary>The notifications bell in the navbar: the signed-in user's recent events and how many are new.</summary>
public class NotificationsViewComponent : ViewComponent
{
    private readonly NotificationService _notifications;

    public NotificationsViewComponent(NotificationService notifications) => _notifications = notifications;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        if (!Guid.TryParse(UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
            return View(new NotificationService.Feed(new(), 0, null));
        return View(await _notifications.ForUserAsync(id, UserClaimsPrincipal.FindFirstValue(ClaimTypes.Role)));
    }
}
