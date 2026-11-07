using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using System.Security.Claims;
using System.Text.Json;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Manages the teacher's personal question library.
/// All write operations are scoped to the authenticated teacher's email.
/// </summary>
[Authorize(Roles = "Teacher")]
public class ContentLibraryController : Controller
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;

    public ContentLibraryController(AppDbContext db, IWebHostEnvironment env)
    {
        _db  = db;
        _env = env;
    }

    private string CurrentUserEmail =>
        User.FindFirstValue(ClaimTypes.Email) ?? User.Identity?.Name ?? "guest";

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private Task<Question?> FindOwnedQuestionAsync(Guid id)
        => _db.Questions.FirstOrDefaultAsync(q => q.Id == id && q.CreatedByEmail == CurrentUserEmail);

    /// <summary>
    /// Reads the hidden state the Create page's canvas/table panels serialise for
    /// "visual" question types and folds it into one JSON blob to store on the
    /// question. Each panel already produces its own shape — this just picks the
    /// one matching <paramref name="type"/> (or assembles it from loose fields for
    /// the panels that don't keep a single hidden JSON field) so none of it is
    /// silently dropped by model binding, which only knows the plain columns.
    /// </summary>
    /// <param name="isEdit">
    /// True when called from the Edit page. Its Table/Vertical/Fraction panels
    /// render with baked-in placeholder values (a default "2 rows / 4 cols / 3
    /// shaded", a default "+" operator, two blank-but-present table cells) and
    /// never pre-fill from the question's existing VisualState, so their fields
    /// always post *something* whether or not the teacher touched that panel.
    /// Treating that as real input would silently overwrite a saved answer grid
    /// with the defaults on any unrelated edit (e.g. just changing difficulty) —
    /// so those three types are skipped here until Edit gains pre-population.
    /// </param>
    private string? BuildVisualState(QuestionType type, bool isEdit = false)
    {
        string? Raw(string key) =>
            Request.Form.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.ToString() : null;

        switch (type)
        {
            case QuestionType.Graphing:
                return Raw("graphState");
            case QuestionType.GeometryDrawing:
                return Raw("geoState");
            case QuestionType.NumberLine:
            {
                // The canvas tool (Create page) posts one JSON blob; the simpler
                // Edit page instead posts start/end/step/answer as loose fields —
                // translate those into the same {min,max,step} shape the student
                // widget reads (elements/answer stays teacher-only, same as the
                // canvas tool's own answer points never reaching the student).
                var canvasState = Raw("nlState");
                if (canvasState is not null) return canvasState;

                var startStr = Raw("nlStart");
                var endStr   = Raw("nlEnd");
                var stepStr  = Raw("nlStep");
                var answer   = Raw("nlAnswer");
                if (startStr is null && endStr is null && stepStr is null && answer is null) return null;

                var elements = new List<object>();
                if (answer is not null)
                    foreach (var part in answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (double.TryParse(part, out var px))
                            elements.Add(new { type = "point", x = px });

                return JsonSerializer.Serialize(new
                {
                    min = double.TryParse(startStr, out var minV) ? minV : (double?)null,
                    max = double.TryParse(endStr,   out var maxV) ? maxV : (double?)null,
                    step = double.TryParse(stepStr, out var stepV) ? stepV : (double?)null,
                    elements
                });
            }
            case QuestionType.DotPlot:
                return Raw("dpState");
            case QuestionType.Protractor:
                return Raw("protState");

            case QuestionType.TableCheck:
            case QuestionType.TableText:
            {
                if (isEdit) return null;
                var headers = Request.Form.TryGetValue("tableHeaders", out var h) ? h.ToArray() : Array.Empty<string>();
                var cells   = Request.Form.TryGetValue("tableCell",    out var c) ? c.ToArray() : Array.Empty<string>();
                if (headers.Length == 0 && cells.Length == 0) return null;

                var rows = new List<string?[]>();
                for (int i = 0; i < cells.Length; i += Math.Max(headers.Length, 1))
                    rows.Add(cells.Skip(i).Take(Math.Max(headers.Length, 1)).ToArray());

                // Per-column: shown to the student as-is ("given"), or left blank
                // as the cell they need to fill in (the default — every column
                // used to behave this way before this toggle existed).
                var given = new bool[headers.Length];
                for (int col = 0; col < headers.Length; col++)
                    given[col] = Request.Form.ContainsKey($"tableColGiven_{col}");

                return JsonSerializer.Serialize(new { headers, rows, given });
            }

            case QuestionType.VerticalOperation:
            {
                if (isEdit) return null;
                var op       = Raw("vertOp");
                var operands = Request.Form.TryGetValue("vertOperands", out var o) ? o.ToArray() : Array.Empty<string>();
                if (op is null && operands.Length == 0) return null;
                return JsonSerializer.Serialize(new { op, operands });
            }

            case QuestionType.FractionModel:
            {
                if (isEdit) return null;
                var rows   = Raw("fracRows");
                var cols   = Raw("fracCols");
                var shaded = Raw("fracShaded");
                if (rows is null && cols is null && shaded is null) return null;
                return JsonSerializer.Serialize(new { rows, cols, shaded });
            }

            default:
                return null;
        }
    }

    // ── GET /ContentLibrary ───────────────────────────────────────────────────

    public async Task<IActionResult> Index(
        string? subject    = null,
        int?    grade      = null,
        string? difficulty = null,
        string? search     = null,
        Guid?   assignmentId = null)
    {
        // A teacher's own questions, plus anything another teacher chose to
        // share ("Make visible to other teachers" — IsPublic). Without this,
        // every teacher could browse every other teacher's private library,
        // answer keys included.
        var query = _db.Questions.Where(q => q.CreatedByEmail == CurrentUserEmail || q.IsPublic);

        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(q => q.Subject == subject);

        if (grade.HasValue)
            query = query.Where(q => q.GradeLevel == grade);

        if (!string.IsNullOrWhiteSpace(difficulty) &&
            Enum.TryParse<DifficultyLevel>(difficulty, out var diff))
            query = query.Where(q => q.Difficulty == diff);

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(q => q.Title.Contains(search));

        var questions = await query
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync();

        ViewBag.Subjects = await _db.Questions
            .Where(q => (q.CreatedByEmail == CurrentUserEmail || q.IsPublic) && q.Subject != null)
            .Select(q => q.Subject!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync();

        ViewBag.CurrentSubject    = subject;
        ViewBag.CurrentGrade      = grade;
        ViewBag.CurrentDifficulty = difficulty;
        ViewBag.CurrentSearch     = search;
        ViewBag.AssignmentId      = assignmentId;

        return View(questions);
    }

    // ── GET /ContentLibrary/Create ────────────────────────────────────────────

    public IActionResult Create(Guid? assignmentId = null, int? defaultType = null)
    {
        ViewBag.AssignmentId = assignmentId;
        ViewBag.DefaultType  = defaultType;
        return View();
    }

    // ── POST /ContentLibrary/Create ───────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string          title,
        QuestionType    type,
        DifficultyLevel difficulty,
        string?         subject,
        int?            gradeLevel,
        string          answerKey,
        string?         explanation,
        bool            isPublic,
        List<string>?   options,
        List<Guid>?     skillIds)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            ViewBag.Error = "Question text is required.";
            return View();
        }

        if (string.IsNullOrWhiteSpace(answerKey))
        {
            ViewBag.Error = "Answer key is required.";
            return View();
        }

        var question = new Question
        {
            Title          = Services.HtmlText.StripHtml(title),
            Type           = type,
            Difficulty     = difficulty,
            Subject        = subject?.Trim(),
            GradeLevel     = gradeLevel,
            AnswerKey      = answerKey.Trim(),
            Explanation    = explanation?.Trim(),
            IsPublic       = isPublic,
            CreatedByEmail = CurrentUserEmail,
            Options        = JsonSerializer.Serialize(options  ?? new List<string>()),
            SkillIds       = JsonSerializer.Serialize(skillIds ?? new List<Guid>()),
            VisualState    = BuildVisualState(type)
        };

        _db.Questions.Add(question);
        await _db.SaveChangesAsync();

        ActivityLog.Record(_env, "QuestionCreated", "Teacher", CurrentUserId, $"question={question.Id}");

        // If opened from assignment editor → attach and signal the parent frame
        if (Request.Form.TryGetValue("assignmentId", out var rawId)
            && Guid.TryParse(rawId, out var assignmentId)
            && assignmentId != Guid.Empty)
        {
            var assignment = await _db.Assignments
                .FirstOrDefaultAsync(a => a.Id == assignmentId
                    && (a.TeacherId == CurrentUserId
                        || a.Class.TeacherId == CurrentUserId
                        || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

            if (assignment != null)
            {
                var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                           ?? new List<JsonElement>();

                // Same shape as ImportFromLibrary — options must be carried over or
                // choice questions render as a plain text box for the student.
                var qObj = new
                {
                    id          = question.Id,
                    title       = question.Title,
                    type        = (int)question.Type,
                    difficulty  = question.Difficulty.ToString(),
                    answerKey   = question.AnswerKey,
                    options     = JsonSerializer.Deserialize<List<string>>(question.Options) ?? new List<string>(),
                    visualState = question.VisualState
                };
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(qObj));
                list.Add(doc.RootElement.Clone());

                assignment.Questions = JsonSerializer.Serialize(list);
                assignment.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }

            // Return a tiny page that tells the parent window to reload
            return Content(
                "<script>window.parent.postMessage('questionSaved','*');</script>",
                "text/html");
        }

        TempData["Success"] = "Question added to your library!";
        return RedirectToAction(nameof(Index));
    }

    // ── GET /ContentLibrary/Edit/{id} ─────────────────────────────────────────

    public async Task<IActionResult> Edit(Guid id)
    {
        var question = await FindOwnedQuestionAsync(id);
        if (question is null) return NotFound();

        ViewBag.CurrentOptions = JsonSerializer.Deserialize<List<string>>(question.Options) ?? new();

        return View(question);
    }

    // ── POST /ContentLibrary/Edit/{id} ────────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(
        Guid id, string title, QuestionType type,
        DifficultyLevel difficulty, string? subject, int? gradeLevel,
        string answerKey, string? explanation, bool isPublic,
        List<string>? options, List<Guid>? skillIds)
    {
        var question = await FindOwnedQuestionAsync(id);
        if (question is null) return NotFound();

        question.Title       = Services.HtmlText.StripHtml(title);
        question.Type        = type;
        question.Difficulty  = difficulty;
        question.Subject     = subject?.Trim();
        question.GradeLevel  = gradeLevel;
        question.AnswerKey   = answerKey.Trim();
        question.Explanation = explanation?.Trim();
        question.IsPublic    = isPublic;
        question.Options     = JsonSerializer.Serialize(options  ?? new List<string>());
        question.SkillIds    = JsonSerializer.Serialize(skillIds ?? new List<Guid>());
        // This page's type-specific panels don't cover every visual type (Graphing,
        // GeometryDrawing, DotPlot and Protractor have no editor here yet) — only
        // overwrite when the submitted form actually carried new panel state,
        // otherwise keep whatever the question already had.
        question.VisualState = BuildVisualState(type, isEdit: true) ?? question.VisualState;
        question.UpdatedAt   = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        TempData["Success"] = "Question updated!";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /ContentLibrary/Delete/{id} ──────────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var question = await FindOwnedQuestionAsync(id);
        if (question is null) return NotFound();

        question.DeletedAt = DateTime.UtcNow; // soft delete
        await _db.SaveChangesAsync();

        TempData["Success"] = "Question removed from library.";
        return RedirectToAction(nameof(Index));
    }

    // ── POST /ContentLibrary/AttachToAssignment ────────────────────────────────

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AttachToAssignment(Guid questionId, Guid assignmentId)
    {
        var question   = await FindOwnedQuestionAsync(questionId);
        var assignment = await _db.Assignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId
                && (a.TeacherId == CurrentUserId
                    || a.Class.TeacherId == CurrentUserId
                    || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));
        if (question is null || assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        var qObj = new
        {
            id          = question.Id,
            title       = question.Title,
            type        = question.Type.ToString(),
            difficulty  = question.Difficulty.ToString(),
            answerKey   = question.AnswerKey,
            options     = JsonSerializer.Deserialize<List<string>>(question.Options) ?? new List<string>(),
            visualState = question.VisualState
        };
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(qObj));
        list.Add(doc.RootElement.Clone());

        assignment.Questions = JsonSerializer.Serialize(list);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = $"'{question.Title}' added to assignment.";
        return RedirectToAction("Edit", "Assignments", new { id = assignmentId });
    }

    // ── GET /ContentLibrary/GetQuestions (JSON API for Assignment modal) ───────

    [HttpGet]
    public async Task<IActionResult> GetQuestions(string? subject = null, int? grade = null)
    {
        // Same "own + public" scoping as Index — this JSON feed powers the
        // "import from library" picker in the assignment editor and must not
        // hand another teacher's private questions (answer keys included) to
        // whoever calls it.
        var query = _db.Questions.Where(q => q.CreatedByEmail == CurrentUserEmail || q.IsPublic);

        if (!string.IsNullOrWhiteSpace(subject))
            query = query.Where(q => q.Subject == subject);

        if (grade.HasValue)
            query = query.Where(q => q.GradeLevel == grade);

        if (!string.IsNullOrWhiteSpace(Request.Query["search"]))
        {
            var search = Request.Query["search"].ToString();
            query = query.Where(q => q.Title.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(Request.Query["difficulty"]))
        {
            var diff = Request.Query["difficulty"].ToString();
            if (Enum.TryParse<DifficultyLevel>(diff, out var dl))
                query = query.Where(q => q.Difficulty == dl);
        }

        var result = await query
            .Where(q => q.DeletedAt == null)
            .OrderByDescending(q => q.CreatedAt)
            .Select(q => new
            {
                q.Id,
                q.Title,
                Type       = q.Type.ToString(),
                Difficulty = q.Difficulty.ToString(),
                q.Subject,
                q.GradeLevel,
                q.AnswerKey,
                q.Options
            })
            .ToListAsync();

        return Json(result);
    }

}
