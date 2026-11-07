using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Services;
using System.Security.Claims;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Serves images attached to assignment questions (diagrams and answer-key reference
/// images). These files live outside wwwroot — an assignment can belong to a private
/// class — and reach the browser only through this action, which checks the caller is
/// either the teacher who owns the assignment or a student assigned to it.
/// </summary>
[Authorize]
[Route("uploads/question-files")]
public class QuestionFilesController : Controller
{
    private readonly AppDbContext        _db;
    private readonly IWebHostEnvironment _env;

    public QuestionFilesController(AppDbContext db, IWebHostEnvironment env)
    {
        _db  = db;
        _env = env;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("{assignmentId:guid}/{questionId}/{fileName}")]
    public async Task<IActionResult> Get(Guid assignmentId, string questionId, string fileName)
    {
        // Same reasoning as SubmissionFilesController: NotFound() instead of Forbid()
        // so an <img> tag degrades to a broken image rather than a sign-in redirect.
        if (!await CanViewAssignmentAsync(assignmentId))
            return NotFound();

        var webPath = $"{QuestionAttachmentStorage.WebPrefix}/{assignmentId}/{questionId}/{fileName}";
        var path    = QuestionAttachmentStorage.ResolveFromWebPath(_env, webPath);
        if (path is null)
            return NotFound();

        return PhysicalFile(path, SubmissionStorage.ContentTypeFor(path));
    }

    /// <summary>The owning teacher sees it; a student assigned to the assignment sees it.</summary>
    private async Task<bool> CanViewAssignmentAsync(Guid assignmentId)
    {
        var userId = CurrentUserId;

        return await _db.Assignments.AnyAsync(a => a.Id == assignmentId
                    && (a.TeacherId == userId
                        || a.Class.TeacherId == userId
                        || a.Class.CoTeachers.Any(ct => ct.TeacherId == userId)))
            || await _db.AssignmentStudents.AnyAsync(
                   asn => asn.AssignmentId == assignmentId && asn.StudentId == userId);
    }
}
