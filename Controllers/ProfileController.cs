using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Services;
using SmartSchoolAI.ViewModels;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

[Authorize]
public class ProfileController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public ProfileController(AppDbContext db, IWebHostEnvironment env)
    {
        _db  = db;
        _env = env;
    }

    // ── GET /Profile ──────────────────────────────────────────────────────────
    public async Task<IActionResult> Index()
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToAction("Login", "Auth");

        var vm = BuildViewModel(user);
        return View(vm);
    }

    // ── POST /Profile/Save ────────────────────────────────────────────────────
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(string firstName, string lastName,
                                          string email, string? school, string? bio)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToAction("Login", "Auth");

        // Validate email uniqueness (if changed)
        var normalised = email?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalised != user.Email)
        {
            var taken = await _db.Users.AnyAsync(u => u.Email == normalised && u.Id != user.Id);
            if (taken)
            {
                TempData["Error"] = "That email address is already in use.";
                return RedirectToAction(nameof(Index));
            }
        }

        user.FullName  = $"{firstName?.Trim()} {lastName?.Trim()}".Trim();
        user.Email     = normalised;
        user.School    = school?.Trim();
        user.Bio       = bio?.Trim();
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["Success"] = "Profile updated successfully!";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /Profile/UploadPhoto ─────────────────────────────────────────────
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadPhoto(IFormFile photo)
    {
        var user = await GetCurrentUserAsync();
        if (user is null) return RedirectToAction("Login", "Auth");

        if (photo is null || photo.Length == 0)
        {
            TempData["Error"] = "Please select a valid image file.";
            return RedirectToAction(nameof(Index));
        }

        var allowed = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
        var ext     = Path.GetExtension(photo.FileName).ToLowerInvariant();

        if (!allowed.Contains(ext))
        {
            TempData["Error"] = "Only JPG, PNG, GIF or WEBP images are accepted.";
            return RedirectToAction(nameof(Index));
        }

        if (photo.Length > 2 * 1024 * 1024)
        {
            TempData["Error"] = "Image must be smaller than 2 MB.";
            return RedirectToAction(nameof(Index));
        }

        byte[] bytes;
        await using (var uploadStream = photo.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await uploadStream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        if (!FileSignature.IsSupportedImage(bytes))
        {
            TempData["Error"] = "File is not a valid image.";
            return RedirectToAction(nameof(Index));
        }

        // Replace any previous custom avatar, then save the new one to protected
        // storage (outside wwwroot — served only to signed-in users).
        AvatarStorage.DeleteByWebPath(_env, user.AvatarUrl);
        user.AvatarUrl = await AvatarStorage.SaveAsync(_env, bytes, ext, user.Id);
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["Success"] = "Profile photo updated!";
        return RedirectToAction(nameof(Index));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private async Task<SmartSchoolAI.Models.User?> GetCurrentUserAsync()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idStr, out var id)) return null;
        return await _db.Users.Include(u => u.Role).FirstOrDefaultAsync(u => u.Id == id);
    }

    private static ProfileViewModel BuildViewModel(SmartSchoolAI.Models.User user)
    {
        var parts     = user.FullName.Split(' ', 2);
        var firstName = parts.Length > 0 ? parts[0] : string.Empty;
        var lastName  = parts.Length > 1 ? parts[1] : string.Empty;

        return new ProfileViewModel
        {
            FirstName = firstName,
            LastName  = lastName,
            FullName  = user.FullName,
            Email     = user.Email,
            School    = user.School,
            Bio       = user.Bio,
            AvatarUrl = user.AvatarUrl,
            Role      = user.Role?.Name ?? "Teacher",
        };
    }
}
