using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Serves user profile photos, which now live outside wwwroot (see
/// <see cref="AvatarStorage"/>). Any signed-in user may fetch any avatar — they
/// appear throughout the app (nav bar, class rosters, a teacher reviewing a
/// student) — but anonymous callers can no longer read them straight off disk.
///
/// The route matches the <c>/uploads/avatars/…</c> paths already stored on users,
/// so nothing in the database or the views has to change.
/// </summary>
[Authorize]
[Route("uploads/avatars")]
public class AvatarFilesController : Controller
{
    private readonly IWebHostEnvironment _env;

    public AvatarFilesController(IWebHostEnvironment env) => _env = env;

    [HttpGet("{fileName}")]
    public IActionResult Photo(string fileName)
    {
        var path = AvatarStorage.FindFile(_env, fileName);
        if (path is null)
            return NotFound();

        return PhysicalFile(path, AvatarStorage.ContentTypeFor(path));
    }
}
