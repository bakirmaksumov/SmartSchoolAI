using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Services;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Serves the photos students upload with their solutions.
///
/// These files used to sit under wwwroot, where the static-file middleware handed
/// them to anyone holding the URL. They now live outside the web root and reach the
/// browser only through this action, which answers one question first: is the caller
/// the student who submitted the work, or the teacher responsible for that student?
///
/// The route matches the paths already stored on answers, so existing submissions
/// keep rendering without touching the database.
/// </summary>
[Authorize]
[Route("uploads/submissions")]
public class SubmissionFilesController : Controller
{
    private readonly AppDbContext        _db;
    private readonly IWebHostEnvironment _env;

    public SubmissionFilesController(AppDbContext db, IWebHostEnvironment env)
    {
        _db  = db;
        _env = env;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("{studentId:guid}/{fileName}")]
    public async Task<IActionResult> Photo(Guid studentId, string fileName)
    {
        // Someone else's work answers the same as a file that isn't there: a signed-in
        // caller learns nothing about what other students have submitted. Forbid() is
        // avoided on purpose — it redirects to the sign-in page, which would hand an
        // <img> tag a page of HTML instead of an image.
        if (!await CanViewWorkOfAsync(studentId))
            return NotFound();

        var path = SubmissionStorage.FindFile(_env, studentId, fileName);
        if (path is null)
            return NotFound();

        return PhysicalFile(path, SubmissionStorage.ContentTypeFor(path));
    }

    /// <summary>
    /// A student sees their own work; a teacher sees the work of students enrolled
    /// in a class they own. Everyone else is refused.
    /// </summary>
    private async Task<bool> CanViewWorkOfAsync(Guid studentId)
    {
        var userId = CurrentUserId;

        if (userId == studentId)
            return true;

        return User.IsInRole("Teacher")
            && await _db.ClassStudents
                        .AnyAsync(cs => cs.StudentId == studentId
                                     && (cs.Class.TeacherId == userId
                                         || cs.Class.CoTeachers.Any(ct => ct.TeacherId == userId)));
    }
}
