using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// The syllabus library. Administrators import a syllabus PDF, check the topics it found and publish it once;
/// teachers then read the published topics (Catalog) to align their own questions to them.
/// Authorisation is per action: an action-level role list would be combined with (not replace) a controller-level one.
/// </summary>
[Authorize]
public class SyllabusController : Controller
{
    private const long MaxPdfBytes = 20 * 1024 * 1024;
    private readonly SyllabusService _syllabus;

    public SyllabusController(SyllabusService syllabus) => _syllabus = syllabus;

    private Guid? CurrentUserId =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    // GET /Syllabus — every version in the library and the import form.
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Index() => View(await _syllabus.VersionsAsync());

    // POST /Syllabus/Import — reads the PDF into a draft and opens its review.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxPdfBytes + 1024 * 1024)]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Import(IFormFile? file, string? sourceUrl)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "Choose the syllabus PDF to import.";
            return RedirectToAction(nameof(Index));
        }
        if (file.Length > MaxPdfBytes || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "Import a PDF file of up to 20 MB.";
            return RedirectToAction(nameof(Index));
        }

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var result = await _syllabus.ImportAsync(ms.ToArray(), file.FileName, sourceUrl, CurrentUserId);
        if (result.Error is not null || result.Version is null)
        {
            TempData["Error"] = result.Error ?? "The file could not be imported.";
            return RedirectToAction(nameof(Index));
        }
        var v = await _syllabus.VersionAsync(result.Version.Id) ?? result.Version;   // with its components and topics
        if (result.Outcome == SyllabusService.ImportOutcome.SameFile)
            TempData["Info"] = $"This syllabus is already in the library ({v.Years}). Nothing was added; here it is.";
        else if (result.Outcome == SyllabusService.ImportOutcome.DifferentFileSameYears && result.Uploaded is { } up)
        {
            // Not the same file, but the library already holds this programme for these exam years. Say so plainly,
            // show both editions side by side, and change nothing: published topics and question links stay.
            static string Edition(string name, string? docVersion, string sha, int comps, int topics) =>
                $"{name}{(docVersion is null ? "" : $", {docVersion}")}, {comps} components, {topics} topics, SHA-256 {sha[..12]}…";
            TempData["SyllabusDifferentFile"] =
                $"The uploaded file is different from the {v.Years} syllabus already in the library, so nothing was imported or replaced. " +
                $"Kept: {Edition(v.SourceName, v.DocumentVersion, v.SourceSha256, v.Components.Count, v.Components.Sum(c => c.Topics.Count))}" +
                $" ({(v.Status == SyllabusVersionStatus.Published ? "published" : "draft")}, imported {v.ImportedAt:dd MMM yyyy}). " +
                $"Uploaded: {Edition(up.Name, up.DocumentVersion, up.Sha256, up.Components, up.Topics)}. " +
                "Published topics and question links are unchanged. If the uploaded edition is the correct one, correct the topics below.";
        }
        return RedirectToAction(nameof(Review), new { id = result.Version.Id });
    }

    // GET /Syllabus/Review/{id} — the topics found, by component, with their PDF pages; editable.
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Review(Guid id)
    {
        var v = await _syllabus.VersionAsync(id);
        if (v is null) return NotFound();
        ViewBag.LinkCounts = await _syllabus.LinkCountsAsync(id);
        return View(v);
    }

    // POST /Syllabus/Save/{id} — the administrator's corrections; optionally publishes in the same step.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Save(Guid id, bool publish = false)
    {
        var form = Request.Form;
        var edits = new List<SyllabusService.TopicEdit>();
        foreach (var key in form.Keys.Where(k => k.StartsWith("title_")))
        {
            if (!Guid.TryParse(key["title_".Length..], out var topicId)) continue;
            edits.Add(new SyllabusService.TopicEdit(topicId, form[$"number_{topicId}"], form[key].ToString(),
                int.TryParse(form[$"page_{topicId}"], out var p) ? p : null, form[$"remove_{topicId}"] == "true"));
        }
        var additions = new List<SyllabusService.TopicAddition>();
        foreach (var key in form.Keys.Where(k => k.StartsWith("newTitle_")))
        {
            if (!Guid.TryParse(key["newTitle_".Length..], out var compId)) continue;
            additions.Add(new SyllabusService.TopicAddition(compId, form[$"newNumber_{compId}"], form[key].ToString(),
                int.TryParse(form[$"newPage_{compId}"], out var p) ? p : null));
        }

        var error = await _syllabus.SaveEditsAsync(id, edits, additions);
        if (error is not null)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Review), new { id });
        }
        if (publish)
        {
            await _syllabus.PublishAsync(id);
            TempData["Success"] = "Published. Teachers can now link their questions to these topics.";
        }
        else TempData["Success"] = "Changes saved.";
        return RedirectToAction(nameof(Review), new { id });
    }

    // POST /Syllabus/Discard/{id} — removes a draft that should not be published.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Discard(Guid id)
    {
        if (await _syllabus.DiscardDraftAsync(id)) TempData["Success"] = "Draft discarded.";
        else TempData["Error"] = "Only a draft can be discarded; a published version stays in the library.";
        return RedirectToAction(nameof(Index));
    }

    // GET /Syllabus/Catalog — published versions, components and topics for the question form's topic picker.
    [HttpGet]
    [Authorize(Roles = "Teacher,Admin")]
    public async Task<IActionResult> Catalog()
    {
        Response.Headers.CacheControl = "no-store";
        return Json(new { versions = await _syllabus.CatalogAsync() });
    }
}
