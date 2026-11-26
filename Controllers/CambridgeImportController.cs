using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// "Import Cambridge Paper" in the Content Library: a four-step wizard (Question Paper → Mark Scheme → Review answer
/// keys → Finish). Each import belongs to the teacher who started it; any other user gets 404 for its pages, PDFs,
/// figures and keys. Readings run in the background and are stored, so reloading or coming back days later continues
/// where the teacher stopped without reading anything again.
/// </summary>
[Authorize(Roles = "Teacher")]
[Route("CambridgeImport")]
public class CambridgeImportController : Controller
{
    private const long MaxPdfBytes = 30 * 1024 * 1024;
    private readonly CambridgeImportService _imports;
    private readonly IServiceScopeFactory _scopes;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CambridgeImportController> _logger;

    public CambridgeImportController(CambridgeImportService imports, IServiceScopeFactory scopes, IWebHostEnvironment env,
                                     ILogger<CambridgeImportController> logger)
    {
        _imports = imports;
        _scopes = scopes;
        _env = env;
        _logger = logger;
    }

    private Guid CurrentUserId => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static readonly string[] StepNames = { "Question Paper", "Mark Scheme", "Review answer keys", "Finish" };

    /// <summary>Where "Continue import" goes: the step after the last one completed.</summary>
    public static string ContinueAction(CambridgeImport i) => i.LastCompletedStep switch
    {
        >= 3 => i.Parts.Any(p => p.Status != ImportPartStatus.Saved) ? nameof(Review) : nameof(Finish),
        2 => nameof(Review),
        1 => nameof(MarkScheme),
        _ => nameof(Paper),
    };

    private async Task<CambridgeImport?> Mine(Guid id) => await _imports.LoadAsync(id, CurrentUserId);

    /// <summary>Runs a reading in the background with its own scope; the page polls <see cref="Status"/>.</summary>
    private void Queue(Guid id, bool markScheme)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<CambridgeImportService>();
                if (markScheme) await svc.RunMarkSchemeAsync(id); else await svc.RunPaperAsync(id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cambridge import {Import}: background reading crashed", id);
            }
        });
    }

    /// <summary>The figures already stored for an import (cropped once, then served as images).</summary>
    private HashSet<string> FigureKeys(CambridgeImport i) =>
        i.Parts.Select(p => p.FigureKey).Where(k => k is not null).Distinct()
         .Where(k => LibraryFileStorage.FigurePath(_env, i.Id, k!) is { } path && System.IO.File.Exists(path)).Select(k => k!).ToHashSet();

    private static async Task<byte[]?> ReadPdf(IFormFile? file)
    {
        if (file is null || file.Length == 0 || file.Length > MaxPdfBytes) return null;
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms);
        var bytes = ms.ToArray();
        return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : null;
    }

    // ── Start ────────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ViewBag.Drafts = await _imports.DraftsAsync(CurrentUserId);
        return View();
    }

    [HttpPost("Start")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxPdfBytes + 1024 * 1024)]
    public async Task<IActionResult> Start(IFormFile? paper)
    {
        var pdf = await ReadPdf(paper);
        if (pdf is null)
        {
            TempData["Error"] = "Choose the Question Paper as a PDF file of up to 30 MB.";
            return RedirectToAction(nameof(Index));
        }
        var r = await _imports.StartAsync(CurrentUserId, User.LibraryOwnerKey(), pdf, paper!.FileName);
        if (r.Error is not null || r.Import is null)
        {
            TempData["Error"] = r.Error ?? "The paper could not be imported.";
            return RedirectToAction(nameof(Index));
        }
        if (r.Existing)
        {
            var existing = (await Mine(r.Import.Id))!;
            TempData["Info"] = "You have already imported this paper. Continue where you stopped — nothing was read again.";
            return RedirectToAction(ContinueAction(existing), new { id = existing.Id });
        }
        if (await _imports.BeginAsync(r.Import, markScheme: false)) Queue(r.Import.Id, markScheme: false);
        return RedirectToAction(nameof(Paper), new { id = r.Import.Id });
    }

    [HttpGet("{id:guid}/Status")]
    public async Task<IActionResult> Status(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        Response.Headers.CacheControl = "no-store";
        return Json(new
        {
            paper = i.PaperStage.ToString(), paperError = i.PaperError,
            markScheme = i.MarkSchemeStage.ToString(), markSchemeError = i.MarkSchemeError,
        });
    }

    // ── Step 1 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/Paper")]
    public async Task<IActionResult> Paper(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        ViewBag.Step = 1;
        ViewBag.Figures = FigureKeys(i);
        return View(i);
    }

    [HttpPost("{id:guid}/RetryPaper")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryPaper(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        if (i.PaperStage != ImportStage.Done && await _imports.BeginAsync(i, markScheme: false)) Queue(i.Id, markScheme: false);
        return RedirectToAction(nameof(Paper), new { id });
    }

    [HttpPost("{id:guid}/Details")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Details(Guid id, int? component, string? variant, string? session, int? year, string? subject)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        await _imports.SaveDetailsAsync(i, component, variant, session, year, subject);
        TempData["Success"] = "Paper details saved.";
        return RedirectToAction(nameof(Paper), new { id });
    }

    [HttpPost("{id:guid}/Topic")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Topic(Guid id, Guid partId, Guid? syllabusTopicId, string? returnTo)
    {
        var i = await Mine(id);
        var p = i?.Parts.FirstOrDefault(x => x.Id == partId);
        if (i is null || p is null) return NotFound();
        if (!await _imports.SetTopicAsync(p, syllabusTopicId)) TempData["Error"] = "That topic is not in a published syllabus.";
        return returnTo == "review"
            ? RedirectToAction(nameof(Review), new { id, part = partId })
            : Redirect(Url.Action(nameof(Paper), new { id }) + "#part-" + partId);
    }

    /// <summary>"Save draft &amp; finish later" from any step: back to the Content Library, where the draft is listed.</summary>
    [HttpPost("{id:guid}/Later")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Later(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        TempData["Success"] = $"Draft saved: {i.Label}. Continue it from the Content Library at any time.";
        return RedirectToAction("Index", "ContentLibrary");
    }

    // ── Step 2 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/MarkScheme")]
    public async Task<IActionResult> MarkScheme(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        if (i.PaperStage != ImportStage.Done) return RedirectToAction(nameof(Paper), new { id });
        ViewBag.Step = 2;
        return View(i);
    }

    [HttpPost("{id:guid}/MarkScheme")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(MaxPdfBytes + 1024 * 1024)]
    public async Task<IActionResult> UploadMarkScheme(Guid id, IFormFile? markScheme)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        if (i.PaperStage != ImportStage.Done) return RedirectToAction(nameof(Paper), new { id });
        if (i.MarkSchemeStage == ImportStage.Running) return RedirectToAction(nameof(MarkScheme), new { id });
        var pdf = await ReadPdf(markScheme);
        if (pdf is null)
        {
            TempData["Error"] = "Choose the Mark Scheme as a PDF file of up to 30 MB.";
            return RedirectToAction(nameof(MarkScheme), new { id });
        }
        var check = await _imports.AttachMarkSchemeAsync(i, pdf, markScheme!.FileName);
        if (check.Error is not null)
        {
            TempData["Error"] = check.Error;
            return RedirectToAction(nameof(MarkScheme), new { id });
        }
        await StartSchemeIfReady(i);
        return RedirectToAction(nameof(MarkScheme), new { id });
    }

    private async Task StartSchemeIfReady(CambridgeImport i)
    {
        var (ready, _, _) = _imports.SchemeReadiness(i);
        if (ready && i.MarkSchemeStage is ImportStage.NotStarted or ImportStage.Failed && await _imports.BeginAsync(i, markScheme: true))
            Queue(i.Id, markScheme: true);
    }

    [HttpPost("{id:guid}/ConfirmScheme")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmScheme(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        await _imports.ConfirmSchemeDetailsAsync(i);
        await StartSchemeIfReady(i);
        return RedirectToAction(nameof(MarkScheme), new { id });
    }

    [HttpPost("{id:guid}/RetryMarkScheme")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RetryMarkScheme(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        await StartSchemeIfReady(i);
        return RedirectToAction(nameof(MarkScheme), new { id });
    }

    // ── Step 3 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/Review")]
    public async Task<IActionResult> Review(Guid id, Guid? part = null, string? filter = null)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        if (i.MarkSchemeStage != ImportStage.Done) return RedirectToAction(i.PaperStage == ImportStage.Done ? nameof(MarkScheme) : nameof(Paper), new { id });
        ViewBag.Step = 3;
        ViewBag.Figures = FigureKeys(i);
        ViewBag.Filter = filter is "needs" or "open" ? filter : "all";
        ViewBag.Part = part is { } pid ? i.Parts.FirstOrDefault(p => p.Id == pid) : null;
        return View(i);
    }

    private IActionResult BackToPart(Guid id, Guid partId, string? filter) =>
        RedirectToAction(nameof(Review), new { id, part = partId, filter });

    [HttpPost("{id:guid}/Confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(Guid id, Guid partId, string? filter)
    {
        var i = await Mine(id);
        var p = i?.Parts.FirstOrDefault(x => x.Id == partId);
        if (i is null || p is null) return NotFound();
        if (await _imports.ConfirmAsync(p) is { } error) TempData["Error"] = error;
        else
        {
            TempData["Success"] = $"{p.Number} confirmed.";
            // Move on to the next part that still needs the teacher, if any.
            var next = i.Parts.Where(x => x.Order > p.Order).Concat(i.Parts)
                .FirstOrDefault(x => x.Status is ImportPartStatus.NeedsReview or ImportPartStatus.Matched && (filter != "needs" || x.Status == ImportPartStatus.NeedsReview));
            if (next is not null) return BackToPart(id, next.Id, filter);
        }
        return BackToPart(id, partId, filter);
    }

    [HttpPost("{id:guid}/ConfirmMatched")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmMatched(Guid id, List<Guid>? partIds, string? filter)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        var n = await _imports.ConfirmMatchedAsync(i, partIds ?? new());
        TempData["Success"] = n == 1 ? "1 part confirmed." : $"{n} parts confirmed.";
        return RedirectToAction(nameof(Review), new { id, filter });
    }

    [HttpPost("{id:guid}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, Guid partId, string? answerKey, string? criteria, int? marks, string? filter)
    {
        var i = await Mine(id);
        var p = i?.Parts.FirstOrDefault(x => x.Id == partId);
        if (i is null || p is null) return NotFound();
        if (await _imports.EditAsync(p, answerKey, criteria, marks) is { } error) TempData["Error"] = error;
        else TempData["Success"] = $"Answer key for {p.Number} updated. Confirm the part when it is right.";
        return BackToPart(id, partId, filter);
    }

    [HttpPost("{id:guid}/Resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(Guid id, Guid partId, bool useNew, string? filter)
    {
        var i = await Mine(id);
        var p = i?.Parts.FirstOrDefault(x => x.Id == partId);
        if (i is null || p is null) return NotFound();
        await _imports.ResolvePendingAsync(p, useNew);
        TempData["Success"] = useNew ? "The new reading replaced your version." : "Your version was kept.";
        return BackToPart(id, partId, filter);
    }

    [HttpPost("{id:guid}/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(Guid id, List<Guid>? partIds)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        var r = await _imports.SaveConfirmedAsync(i, partIds is { Count: > 0 } ? partIds : null);
        if (r.Saved + r.AlreadySaved == 0)
        {
            TempData["Error"] = "Confirm at least one part before saving.";
            return RedirectToAction(nameof(Review), new { id });
        }
        return RedirectToAction(nameof(Finish), new { id, saved = r.Saved, already = r.AlreadySaved });
    }

    // ── Step 4 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/Finish")]
    public async Task<IActionResult> Finish(Guid id, int? saved = null, int? already = null)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        if (i.LastCompletedStep < 3) return RedirectToAction(ContinueAction(i), new { id });
        ViewBag.Step = 4;
        ViewBag.JustSaved = saved;
        ViewBag.AlreadySaved = already;
        return View(i);
    }

    [HttpPost("{id:guid}/Discard")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Discard(Guid id)
    {
        var i = await Mine(id);
        if (i is null) return NotFound();
        await _imports.DiscardAsync(i);
        TempData["Success"] = "Import discarded. Questions already saved stay in your Content Library.";
        return RedirectToAction("Index", "ContentLibrary");
    }

    // ── Files (owner only) ───────────────────────────────────────────────────────────────────────────────────────
    [HttpGet("{id:guid}/File/{which}")]
    public async Task<IActionResult> File(Guid id, string which)
    {
        var i = await Mine(id);
        if (i is null || which is not ("paper" or "markscheme")) return NotFound();
        var path = LibraryFileStorage.ImportPdfPath(_env, id, which);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(path, "application/pdf");
    }

    [HttpGet("{id:guid}/Figure/{key}")]
    public async Task<IActionResult> Figure(Guid id, string key)
    {
        var i = await Mine(id);
        var path = i is null ? null : LibraryFileStorage.FigurePath(_env, id, key);
        if (path is null || !System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(path, "image/png");
    }

    /// <summary>The figure as cropped from the paper in the teacher's browser (or a picture the teacher chose).</summary>
    [HttpPost("{id:guid}/Figure/{key}")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> UploadFigure(Guid id, string key, IFormFile? image)
    {
        var i = await Mine(id);
        if (i is null || image is null || image.Length == 0 || image.Length > 5 * 1024 * 1024) return NotFound();
        if (!i.Parts.Any(p => p.FigureKey == key) && !System.Text.RegularExpressions.Regex.IsMatch(key, @"^q\d{1,3}$")) return NotFound();
        using var ms = new MemoryStream();
        await image.CopyToAsync(ms);
        var bytes = ms.ToArray();
        // PNG only (the browser crop produces PNG): check the signature, not the name.
        if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47) return BadRequest(new { error = "PNG only." });
        if (!await LibraryFileStorage.SaveFigureAsync(_env, id, key, bytes)) return NotFound();
        // A question that had no figure from the reading gets this one on all its parts.
        if (int.TryParse(key[1..], out var qn))
            foreach (var p in i.Parts.Where(p => p.QuestionNumber == qn && p.FigureKey is null)) p.FigureKey = key;
        await HttpContext.RequestServices.GetRequiredService<AppDbContext>().SaveChangesAsync();
        return Json(new { ok = true, url = Url.Action(nameof(Figure), new { id, key }) + "?v=" + DateTime.UtcNow.Ticks });
    }
}
