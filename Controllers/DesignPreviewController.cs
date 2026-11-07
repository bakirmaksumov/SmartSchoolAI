using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Throwaway design trial: renders the student result screen in the Tabler
/// UI kit so the look can be compared against the current Sneat version.
/// Not linked from the app — open /DesignPreview/StudentResult directly.
/// Delete this controller and Views/DesignPreview if Tabler isn't adopted.
/// </summary>
[AllowAnonymous]
public class DesignPreviewController : Controller
{
    public IActionResult StudentResult() => View();
}
