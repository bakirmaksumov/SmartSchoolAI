using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// School-administrator tools. Currently just one job: reset a teacher's
/// password when they're locked out and have no self-service recovery path
/// (unlike students, who their teacher can already reset via ClassesController).
/// </summary>
[Authorize(Roles = "Admin")]
public class AdminController : Controller
{
    private readonly AppDbContext _db;

    public AdminController(AppDbContext db) => _db = db;

    // GET: /Admin — every teacher account, for the "reset password" tool.
    public async Task<IActionResult> Index(string? search = null)
    {
        var query = _db.Users.Where(u => u.Role.Name == Role.Teacher);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(u => u.FullName.Contains(search) || u.Email.Contains(search));

        var teachers = await query
            .OrderBy(u => u.FullName)
            .ToListAsync();

        ViewBag.Search = search;
        return View(teachers);
    }

    // POST: /Admin/ResetTeacherPassword
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetTeacherPassword(Guid teacherId, string newPassword)
    {
        var teacher = await _db.Users
            .FirstOrDefaultAsync(u => u.Id == teacherId && u.Role.Name == Role.Teacher);

        if (teacher is null) return NotFound();

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            TempData["Error"] = "The new password must be at least 8 characters.";
            return RedirectToAction(nameof(Index));
        }

        teacher.PasswordHash       = PasswordHasher.Hash(newPassword);
        teacher.MustChangePassword = true;
        teacher.UpdatedAt          = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Same one-time plaintext reveal pattern as ClassesController.ResetStudentPassword —
        // rendered by _Layout.cshtml, kept out of the routine Success/Error banners.
        TempData["ResetPasswordFor"]   = teacher.FullName;
        TempData["ResetPasswordPlain"] = newPassword;
        return RedirectToAction(nameof(Index));
    }
}
