using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using System.Security.Claims;

namespace SmartSchoolAI.ViewComponents;

/// <summary>
/// Renders the current user's avatar URL + full name for the navbar.
/// Falls back to the default Sneat avatar if none is uploaded.
/// </summary>
public class UserAvatarViewComponent : ViewComponent
{
    private readonly AppDbContext _db;

    public UserAvatarViewComponent(AppDbContext db) => _db = db;

    public async Task<IViewComponentResult> InvokeAsync()
    {
        var idStr = UserClaimsPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        string avatarUrl = "/sneat/img/avatars/1.png";
        string fullName  = UserClaimsPrincipal.FindFirstValue(ClaimTypes.Name) ?? "User";
        string role      = UserClaimsPrincipal.FindFirstValue(ClaimTypes.Role)  ?? "Teacher";

        if (Guid.TryParse(idStr, out var id))
        {
            var user = await _db.Users
                .AsNoTracking()
                .Select(u => new { u.Id, u.AvatarUrl, u.FullName, Role = u.Role.Name })
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user != null)
            {
                avatarUrl = string.IsNullOrEmpty(user.AvatarUrl)
                    ? "/sneat/img/avatars/1.png"
                    : user.AvatarUrl;
                fullName  = user.FullName;
                role      = user.Role ?? role;
            }
        }

        ViewBag.AvatarUrl = avatarUrl;
        ViewBag.FullName  = fullName;
        ViewBag.Role      = role;

        return View();
    }
}
