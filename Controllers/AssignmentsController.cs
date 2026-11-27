using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.ViewModels;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

using SmartSchoolAI.Services;

namespace SmartSchoolAI.Controllers;

/// <summary>
/// Manages assignments: list (with filters), create, edit (questions + settings),
/// and state transitions (Publish, Close, Delete).
/// All queries are scoped to the currently logged-in teacher.
/// </summary>
[Authorize(Roles = "Teacher")]
public class AssignmentsController : Controller
{
    private readonly AppDbContext _db;
    private readonly PdfImportService _pdfImport;
    private readonly IWebHostEnvironment _env;
    private readonly GenerationJobTracker _jobs;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssignmentsController> _logger;
    private readonly AssignmentDistributionService _distribution;

    public AssignmentsController(
        AppDbContext db, PdfImportService pdfImport, IWebHostEnvironment env,
        GenerationJobTracker jobs, IServiceScopeFactory scopeFactory,
        ILogger<AssignmentsController> logger, AssignmentDistributionService distribution)
    {
        _db           = db;
        _distribution = distribution;
        _pdfImport    = pdfImport;
        _env          = env;
        _jobs         = jobs;
        _scopeFactory = scopeFactory;
        _logger       = logger;
    }

    private Guid CurrentUserId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // GET: /Assignments
    public async Task<IActionResult> Index(Guid? classId = null, string? status = null, int? page = null)
    {
        var query = _db.Assignments
            .Include(a => a.Class)
            .Include(a => a.StudentResults)
            .AccessibleBy(CurrentUserId);

        if (classId.HasValue)
            query = query.Where(a => a.ClassId == classId);

        if (!string.IsNullOrEmpty(status) &&
            Enum.TryParse<AssignmentStatus>(status, ignoreCase: true, out var parsedStatus))
            query = query.Where(a => a.Status == parsedStatus);

        // Ten per page, newest first; Id breaks ties so an item never shows on two pages.
        var (assignments, pageInfo) = await query
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id)
            .ToPageAsync(page);

        return View(new AssignmentListViewModel
        {
            Assignments = assignments,
            Page        = pageInfo,
            Classes = await _db.Classes
                .AccessibleBy(CurrentUserId)
                .OrderBy(c => c.Name)
                .ToListAsync(),
            SelectedClassId = classId,
            SelectedStatus  = status
        });
    }

    // GET: /Assignments/Create?classId=…  — opened from a class page, that class is filled in.
    public async Task<IActionResult> Create(Guid? classId = null)
    {
        await PopulateClassesDropdownAsync(classId);
        return View();
    }

    // POST: /Assignments/Create  →  redirects straight to Edit
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string title, string? description, Guid classId,
        AssignmentType type, DateTime? deadline, bool allowLateSubmission, DateTime? availableFrom = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            ViewBag.Error = "Title is required.";
            await PopulateClassesDropdownAsync(classId);
            return View();
        }

        var cls = await _db.Classes
            .FirstOrDefaultAsync(c => c.Id == classId && (c.TeacherId == CurrentUserId || c.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (cls is null)
        {
            ViewBag.Error = "Selected class not found.";
            await PopulateClassesDropdownAsync();
            return View();
        }

        var assignment = new Assignment
        {
            Title               = title.Trim(),
            Description         = description?.Trim(),
            ClassId             = classId,
            TeacherId           = CurrentUserId,
            Type                = type,
            Deadline            = SchoolTime.ToUtc(deadline),
            AvailableFrom       = SchoolTime.ToUtc(availableFrom),
            AllowLateSubmission = allowLateSubmission,
            Status              = AssignmentStatus.Draft,
            Questions           = "[]"
        };

        _db.Assignments.Add(assignment);
        await _db.SaveChangesAsync();

        ActivityLog.Record(_env, "AssignmentCreated", "Teacher", CurrentUserId, $"assignment={assignment.Id} class={classId}");

        // Go straight to the question editor
        return RedirectToAction(nameof(Edit), new { id = assignment.Id });
    }

    // GET: /Assignments/Edit/id
    public async Task<IActionResult> Edit(Guid id)
    {
        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .FirstOrDefaultAsync(a => a.Id == id && (a.TeacherId == CurrentUserId || a.Class.TeacherId == CurrentUserId || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (assignment is null) return NotFound();

        return View(new AssignmentEditViewModel
        {
            Assignment       = assignment,
            Classes          = await TeacherClassesAsync(),
            CanEditQuestions = assignment.AllowsQuestionEditing(await HasSubmissionsAsync(id)),
            AssignPanel      = await BuildAssignPanelAsync(assignment, "edit")
        });
    }

    // POST: /Assignments/SaveSettings/id  — update title, class, type, deadline, etc.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveSettings(
        Guid id, string title, string? description,
        Guid classId, AssignmentType type, DateTime? deadline,
        bool allowLateSubmission, bool allowSolutionReveal, bool allowInstantFeedback,
        GradingType gradingType, int passThreshold, int maxAttempts, bool antiCopyPaste,
        DateTime? availableFrom = null)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return NotFound();

        if (string.IsNullOrWhiteSpace(title))
        {
            // Back to the settings tab so the teacher can fix it in place.
            TempData["Error"] = "Title is required.";
            return RedirectToAction(nameof(Edit), new { id, tab = "settings" });
        }

        // The target class is client-supplied, so it must be re-checked: without
        // this a teacher could move their assignment into someone else's class.
        var ownsTargetClass = await _db.Classes
            .AnyAsync(c => c.Id == classId && (c.TeacherId == CurrentUserId || c.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (!ownsTargetClass)
        {
            TempData["Error"] = "Selected class not found.";
            return RedirectToAction(nameof(Edit), new { id, tab = "settings" });
        }

        // Recipients and waiting requests belong to the current class; moving the work elsewhere would
        // leave them pointing at students of another class.
        if (classId != assignment.ClassId &&
            (await _db.AssignmentStudents.AnyAsync(x => x.AssignmentId == id) ||
             await _db.LateJoinRequests.AnyAsync(r => r.AssignmentId == id)))
        {
            TempData["Error"] = "This assignment has already been given to students of its class, so it can't be moved to another class.";
            return RedirectToAction(nameof(Edit), new { id, tab = "settings" });
        }

        assignment.Title                = title.Trim();
        assignment.Description          = description?.Trim();
        assignment.ClassId              = classId;
        assignment.Type                 = type;
        assignment.Deadline             = SchoolTime.ToUtc(deadline);
        assignment.AvailableFrom        = SchoolTime.ToUtc(availableFrom);
        assignment.AllowLateSubmission  = allowLateSubmission;
        assignment.AllowSolutionReveal  = allowSolutionReveal;
        assignment.AllowInstantFeedback = allowInstantFeedback;
        assignment.AntiCopyPaste        = antiCopyPaste;
        assignment.GradingType          = gradingType;
        assignment.PassThreshold        = Math.Clamp(passThreshold, 1, 100);
        assignment.MaxAttempts          = Math.Clamp(maxAttempts, 0, 50);   // 0 = unlimited
        assignment.UpdatedAt            = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        TempData["Success"] = $"Settings for '{assignment.Title}' saved.";
        return RedirectToAction(nameof(Index));
    }

    // POST: /Assignments/AddQuestion/id  — appends a question JSON object
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuestion(Guid id, [FromForm] string questionJson)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        if (!TryParseQuestionJson(questionJson, out var newQuestion, out var parseError))
            return BadRequest(new { error = parseError });

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        var updated = list.Select(e => e.Clone()).ToList();
        updated.Add(newQuestion);

        assignment.Questions = JsonSerializer.Serialize(updated);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { count = updated.Count });
    }

    // POST: /Assignments/UpdateQuestion/id  — replaces a question at a given index
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateQuestion(Guid id, int index, [FromForm] string questionJson)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index < 0 || index >= list.Count)
            return BadRequest(new { error = "Invalid question index." });

        if (!TryParseQuestionJson(questionJson, out var replacement, out var parseError))
            return BadRequest(new { error = parseError });

        var updated = list.Select(e => e.Clone()).ToList();
        updated[index] = replacement;

        assignment.Questions = JsonSerializer.Serialize(updated);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { ok = true });
    }

    // POST: /Assignments/RemoveQuestion/id
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveQuestion(Guid id, int index)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index >= 0 && index < list.Count)
        {
            var removedId = list[index].TryGetProperty("id", out var qid) && qid.ValueKind == JsonValueKind.String
                ? qid.GetString() : null;

            if (!string.IsNullOrEmpty(removedId))
                QuestionAttachmentStorage.DeleteQuestionFolder(_env, id, removedId);

            list.RemoveAt(index);

            // An orphaned subpoint (its parent just got removed) doesn't make
            // sense on its own — cascade the delete to every child referencing it.
            if (!string.IsNullOrEmpty(removedId))
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (list[i].TryGetProperty("groupId", out var gid) &&
                        gid.ValueKind == JsonValueKind.String && gid.GetString() == removedId)
                    {
                        if (list[i].TryGetProperty("id", out var cid) && cid.ValueKind == JsonValueKind.String)
                            QuestionAttachmentStorage.DeleteQuestionFolder(_env, id, cid.GetString() ?? "");
                        list.RemoveAt(i);
                    }
                }
            }

            assignment.Questions = JsonSerializer.Serialize(list);
            assignment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        TempData["Success"] = "Question removed.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    // POST: /Assignments/MoveQuestion/id — swaps a top-level question with its
    // neighbouring top-level question. A question with subpoints moves as a
    // block together with them (subpoints aren't individually reorderable),
    // so this groups the flat list into blocks first rather than doing a
    // simple adjacent-index swap, which would otherwise swap a header with
    // some other header's subpoint if one happens to sit next to it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MoveQuestion(Guid id, int index, int direction)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index < 0 || index >= list.Count)
            return RedirectToAction(nameof(Edit), new { id });

        var blocks = BuildQuestionBlocks(list);
        var bi = blocks.FindIndex(b => b[0] == index);
        if (bi < 0)
            return RedirectToAction(nameof(Edit), new { id }); // index was a subpoint, not a block header

        var bj = bi + (direction < 0 ? -1 : 1);
        if (bj >= 0 && bj < blocks.Count)
        {
            (blocks[bi], blocks[bj]) = (blocks[bj], blocks[bi]);

            var reordered = new List<JsonElement>();
            var covered   = new HashSet<int>();
            foreach (var block in blocks)
                foreach (var elementIndex in block)
                {
                    reordered.Add(list[elementIndex]);
                    covered.Add(elementIndex);
                }

            // Defensive: a subpoint whose GroupId doesn't match any surviving
            // question (shouldn't happen — RemoveQuestion cascades) would
            // otherwise silently vanish from the reordered list. Keep it,
            // appended at the end, rather than lose data.
            for (int i = 0; i < list.Count; i++)
                if (!covered.Contains(i))
                    reordered.Add(list[i]);

            assignment.Questions = JsonSerializer.Serialize(reordered);
            assignment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>
    /// Groups a flat question list into blocks of [headerIndex, ...childIndices]
    /// — one block per top-level question (no groupId), with every subpoint that
    /// references it (by GroupId == that question's Id) folded in. A question
    /// with no subpoints is simply a block of one.
    /// </summary>
    private static List<List<int>> BuildQuestionBlocks(List<JsonElement> list)
    {
        static string? IdOf(JsonElement e) =>
            e.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static string? GroupIdOf(JsonElement e) =>
            e.TryGetProperty("groupId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var blocks = new List<List<int>>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!string.IsNullOrEmpty(GroupIdOf(list[i]))) continue; // handled as a child of its parent's block

            var block = new List<int> { i };
            var qid = IdOf(list[i]);
            if (!string.IsNullOrEmpty(qid))
            {
                for (int j = 0; j < list.Count; j++)
                    if (j != i && GroupIdOf(list[j]) == qid)
                        block.Add(j);
            }
            blocks.Add(block);
        }
        return blocks;
    }

    // POST: /Assignments/ToggleRequireWork/id — flips one question's "require work" flag.
    // A dedicated endpoint rather than round-tripping the whole question through
    // UpdateQuestion's questionJson: the toggle lives on the card itself (not the
    // edit drawer), so it only ever needs to flip one field, not resend everything.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ToggleRequireWork(Guid id, int index)
        => ToggleQuestionFlagAsync(id, index, "requireWork");

    // POST: /Assignments/ToggleRequireDrawing/id — flips whether the student answers this
    // question by drawing on a sketch pad (e.g. "sketch the graph on this diagram").
    [HttpPost]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ToggleRequireDrawing(Guid id, int index)
        => ToggleQuestionFlagAsync(id, index, "requireDrawing");

    private async Task<IActionResult> ToggleQuestionFlagAsync(Guid id, int index, string flag)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index >= 0 && index < list.Count)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(list[index].GetRawText())!.AsObject();
            var current = node.TryGetPropertyValue(flag, out var rw) && rw is not null
                          && rw.GetValueKind() == JsonValueKind.True;
            node[flag] = !current;
            list[index] = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

            assignment.Questions = JsonSerializer.Serialize(list);
            assignment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // POST: /Assignments/DismissValidationWarning/id — clears the AI's
    // "this question looks off" flag once the teacher has reviewed it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DismissValidationWarning(Guid id, int index)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index >= 0 && index < list.Count)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(list[index].GetRawText())!.AsObject();
            node["validationWarning"] = null;
            list[index] = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

            assignment.Questions = JsonSerializer.Serialize(list);
            assignment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // POST: /Assignments/DuplicateQuestion/id — inserts a copy right after the original.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DuplicateQuestion(Guid id, int index)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (index >= 0 && index < list.Count)
        {
            // The copy gets its own "id" so removing either one only ever cleans up
            // that question's own attachment folder — sharing the original's id
            // would make deleting one copy silently delete the other's diagram too.
            var originalId = list[index].TryGetProperty("id", out var oid) && oid.ValueKind == JsonValueKind.String
                ? oid.GetString() : null;

            using var doc = JsonDocument.Parse(list[index].GetRawText());
            var node = System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText())!.AsObject();
            var newId = Guid.NewGuid().ToString();
            node["id"] = newId;

            var copy = JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
            var insertAt = index + 1;
            list.Insert(insertAt, copy);
            insertAt++;

            // Duplicating a group header (a question with subpoints) also
            // duplicates each subpoint, re-pointed at the new header's id —
            // otherwise the copy would silently drop its 2a/2b parts.
            if (!string.IsNullOrEmpty(originalId))
            {
                var children = list
                    .Where((e, i) => i != index && i != index + 1 &&
                        e.TryGetProperty("groupId", out var gid) &&
                        gid.ValueKind == JsonValueKind.String && gid.GetString() == originalId)
                    .Select(e => e.Clone())
                    .ToList();

                foreach (var child in children)
                {
                    var childNode = System.Text.Json.Nodes.JsonNode.Parse(child.GetRawText())!.AsObject();
                    childNode["id"]      = Guid.NewGuid().ToString();
                    childNode["groupId"] = newId;
                    list.Insert(insertAt, JsonDocument.Parse(childNode.ToJsonString()).RootElement.Clone());
                    insertAt++;
                }
            }

            assignment.Questions = JsonSerializer.Serialize(list);
            assignment.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    // POST: /Assignments/AddQuestionWithParts/id — adds a question with parts (a), (b), …
    // in one step: a parent with no text yet and the given number of empty parts.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuestionWithParts(Guid id, int parts = 2)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        // The parent holds the shared text, which may stay empty: many exam questions go straight into "(a)".
        var parentId = Guid.NewGuid().ToString();
        list.Add(JsonSerializer.SerializeToElement(new { id = parentId, title = "", type = "0", answerKey = "" }));
        for (var p = 0; p < Math.Clamp(parts, 2, 8); p++)
            list.Add(JsonSerializer.SerializeToElement(new
            {
                id = Guid.NewGuid().ToString(), title = "", type = "0", answerKey = "", marks = 1, groupId = parentId
            }));

        assignment.Questions = JsonSerializer.Serialize(list);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        TempData["Success"] = "Question with parts added. Click Edit on each part to write it and set its marks.";
        return RedirectToAction(nameof(Edit), new { id });
    }

    // POST: /Assignments/AddSubpoint/id — adds a new subpoint (e.g. "2a") under
    // an existing question, inserted right after that question's last existing
    // subpoint (or right after the question itself if it has none yet).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddSubpoint(Guid id, int parentIndex)
    {
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();

        if (parentIndex < 0 || parentIndex >= list.Count)
            return RedirectToAction(nameof(Edit), new { id });

        var parentId = list[parentIndex].TryGetProperty("id", out var pid) && pid.ValueKind == JsonValueKind.String
            ? pid.GetString() : null;

        if (string.IsNullOrEmpty(parentId))
            return RedirectToAction(nameof(Edit), new { id });

        // Insert after the parent's last existing subpoint, wherever in the
        // list that happens to be, so repeated "Add subpoint" clicks append
        // in order instead of always landing right after the parent itself.
        var insertAt = parentIndex + 1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].TryGetProperty("groupId", out var gid) &&
                gid.ValueKind == JsonValueKind.String && gid.GetString() == parentId)
            {
                insertAt = Math.Max(insertAt, i + 1);
            }
        }

        var newQuestion = new
        {
            id         = Guid.NewGuid().ToString(),
            title      = "",
            type       = "0", // ShortAnswer
            answerKey  = "",
            groupId    = parentId
        };
        list.Insert(insertAt, JsonSerializer.SerializeToElement(newQuestion));

        assignment.Questions = JsonSerializer.Serialize(list);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Edit), new { id });
    }

    // GET: /Assignments/Preview/id  — renders the assignment as a student sees it (read-only).
    public async Task<IActionResult> Preview(Guid id)
    {
        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .FirstOrDefaultAsync(a => a.Id == id && (a.TeacherId == CurrentUserId || a.Class.TeacherId == CurrentUserId || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (assignment is null) return NotFound();

        var questions = JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>(
                            assignment.Questions) ?? new();

        ViewBag.Assignment    = assignment;
        ViewBag.AttemptNumber = 1;
        ViewBag.IsPreview     = true;

        // Reuse the exact student-facing view so the preview is pixel-identical.
        return View("~/Views/Student/TakeAssignment.cshtml", questions);
    }

    /// <summary>The assignment with everything its page and the live board need, if this teacher may see it.</summary>
    private Task<Assignment?> LoadForProgressAsync(Guid id)
        => _db.Assignments
            .Include(a => a.Class)
                .ThenInclude(c => c.ClassStudents)
                    .ThenInclude(cs => cs.Student)
            .Include(a => a.StudentResults)
                .ThenInclude(sr => sr.Student)
            .Include(a => a.StudentResults)
                .ThenInclude(sr => sr.StudentAnswers)
            .Include(a => a.AssignmentStudents)
                .ThenInclude(asn => asn.Student)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id && (a.TeacherId == CurrentUserId || a.Class.TeacherId == CurrentUserId || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

    // GET: /Assignments/Details/id?tab=students|questions|details&filter=all|submitted|not-submitted|needs-review
    // With fragment=live, only the counters and student rows (the live board swaps them in place).
    public async Task<IActionResult> Details(Guid id, string tab = "students", string filter = "all", string? fragment = null)
    {
        var assignment = await LoadForProgressAsync(id);
        if (assignment is null) return NotFound();

        var progress = AssignmentProgress.Build(assignment);
        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();

        var model = new AssignmentDetailsViewModel
        {
            Assignment       = assignment,
            Progress         = progress,
            Tab              = tab is "questions" or "details" ? tab : "students",
            Filter           = filter is "submitted" or "not-submitted" or "needs-review" ? filter : "all",
            CanEditQuestions = assignment.AllowsQuestionEditing(assignment.StudentResults.Any(r => r.IsSubmitted)),
            QuestionCount    = questions.Count(q => string.IsNullOrEmpty(q.Id) || !questions.Any(x => x.GroupId == q.Id)),
            TotalMarks       = GradingService.TotalMarks(questions)
        };

        if (fragment == "live")
            return PartialView("_DetailsLive", model);

        var members = assignment.Class.ClassStudents.Select(cs => cs.StudentId).ToHashSet();
        model.PendingRequests = (await _db.LateJoinRequests
            .Where(r => r.AssignmentId == id && r.Status == LateJoinRequestStatus.Pending)
            .Select(r => r.StudentId)
            .ToListAsync()).Count(members.Contains);
        model.QuestionStats = BuildQuestionStatsForRecipients(questions, progress);
        model.AssignPanel   = assignment.Status is AssignmentStatus.Draft or AssignmentStatus.Active
                                  ? await BuildAssignPanelAsync(assignment, "details")
                                  : null;
        return View(model);
    }

    /// <summary>
    /// How each question went, over the newest submitted attempt of each current recipient — so every student
    /// counts once, a retake replaces the earlier try, and former recipients are left out. Shared stems are kept
    /// as headings (nothing to answer) so parts read in context.
    /// </summary>
    public static List<QuestionStatDto> BuildQuestionStatsForRecipients(List<QuestionDto> questions, AssignmentProgress progress)
    {
        var layout  = QuestionLayout.Compute(questions);
        var latest  = progress.Rows.Select(r => r.Latest).Where(r => r is not null).Cast<StudentResult>().ToList();

        return questions.Select((q, idx) =>
        {
            var answers = latest
                .Select(r => r.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == idx))
                .Where(a => a is not null && (!string.IsNullOrWhiteSpace(a.AnswerValue)
                                              || !string.IsNullOrEmpty(a.ImagePath) || !string.IsNullOrEmpty(a.SketchPath)))
                .Cast<StudentAnswer>()
                .ToList();
            var correct = answers.Count(a => a.IsCorrect == true);
            return new QuestionStatDto
            {
                Index     = idx,
                Label     = layout.Labels[idx],
                IsStem    = layout.IsStem[idx],
                IsPart    = layout.ParentOf[idx] >= 0,
                Title     = q.Title,
                Students  = latest.Count,
                Answered  = answers.Count,
                Correct   = correct,
                NotMarked = answers.Count(a => a.IsCorrect is null),
                SolveRate = answers.Count > 0 ? (int)Math.Round(correct * 100.0 / answers.Count) : -1
            };
        }).ToList();
    }

    // POST: /Assignments/Assign/id — the single "Assign to N students" action: saves who gets the work and,
    // for a draft, makes it active, all in one transaction. On an error nothing changes and the teacher's
    // choices are shown again so they can retry.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Assign(
        Guid id, AssignmentAudience audience, List<Guid>? studentIds, LateJoinPolicy lateJoin,
        DateTime? availableFrom, DateTime? deadline, string returnTo = "edit")
    {
        var request = new AssignmentDistributionService.AssignRequest(
            audience, studentIds ?? new List<Guid>(), lateJoin,
            SchoolTime.ToUtc(availableFrom), SchoolTime.ToUtc(deadline));

        var outcome = await _distribution.AssignAsync(id, CurrentUserId, request);
        returnTo = returnTo == "details" ? "details" : "edit";

        if (!outcome.Success)
        {
            if (outcome.ClassId == Guid.Empty) return NotFound();

            TempData["AssignError"]   = outcome.Error;
            TempData["AssignAttempt"] = JsonSerializer.Serialize(new AssignAttemptDto
            {
                Audience      = audience,
                StudentIds    = request.StudentIds.ToList(),
                LateJoin      = lateJoin,
                AvailableFrom = request.AvailableFrom,
                Deadline      = request.Deadline
            });
            return Redirect(returnTo == "details"
                ? $"/Assignments/Details/{id}?assign=open"
                : $"/Assignments/Edit/{id}?assign=open");
        }

        ActivityLog.Record(_env, "AssignmentAssigned", "Teacher", CurrentUserId,
            $"assignment={id} audience={audience} recipients={outcome.Recipients} added={outcome.Added}");

        var who = outcome.Recipients == 1 ? "1 student" : $"{outcome.Recipients} students";
        var message = outcome.Published
            ? $"Assigned to {who} from {outcome.ClassName}."
            : outcome.Added == 0
                ? $"No one new to add — it is assigned to {who} from {outcome.ClassName}."
                : $"Added {(outcome.Added == 1 ? "1 student" : $"{outcome.Added} students")} — now assigned to {who} from {outcome.ClassName}.";
        if (outcome.OpensAt is { } opens)
            message += $" Students can start on {SchoolTime.Show(opens)} ({SchoolTime.Label}).";

        TempData["Success"] = message;
        return RedirectToAction(nameof(Details), new { id });
    }

    // POST: /Assignments/SetLateJoinPolicy/id — the explicit switch for students who join the class later.
    // Affects future joins only; nobody already in the class is added by it.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetLateJoinPolicy(Guid id, LateJoinPolicy policy)
    {
        if (!await _distribution.SetLateJoinPolicyAsync(id, CurrentUserId, policy)) return NotFound();

        TempData["Success"] = policy switch
        {
            LateJoinPolicy.AutoAssign      => "Students who join the class from now on get this assignment automatically.",
            LateJoinPolicy.TeacherApproval => "Students who join the class from now on wait for your approval for this assignment.",
            _                              => "Students who join the class later are no longer given this assignment."
        };
        return RedirectToAction(nameof(Details), new { id });
    }

    // GET: /Assignments/Review — submissions needing review: the list behind the dashboard's counter.
    public async Task<IActionResult> Review()
        => View(new ReviewQueueViewModel
        {
            Results = await ReviewQueueQuery(_db, CurrentUserId)
                .Include(r => r.Student)
                .Include(r => r.StudentAnswers)
                .Include(r => r.Assignment).ThenInclude(a => a.Class)
                .OrderBy(r => r.SubmittedAt)
                .ToListAsync()
        });

    /// <summary>Submissions waiting for this teacher's review; the dashboard counts the same query.</summary>
    public static IQueryable<StudentResult> ReviewQueueQuery(AppDbContext db, Guid teacherId)
        => db.StudentResults
             .Where(ReviewRules.NeedsReview)
             .Where(r => r.Assignment.DeletedAt == null
                      && (r.Assignment.TeacherId == teacherId
                          || r.Assignment.Class.TeacherId == teacherId
                          || r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == teacherId)));

    /// <summary>
    /// The assign panel's starting state. A draft nobody has been chosen for suggests the whole class and the
    /// rule matching its type; a draft with chosen students (including one from before this panel existed)
    /// shows exactly those; an active assignment shows who already has it. After a failed attempt the
    /// teacher's own choices come back.
    /// </summary>
    private async Task<AssignPanelViewModel> BuildAssignPanelAsync(Assignment assignment, string returnTo)
    {
        var members = await _db.ClassStudents
            .Where(cs => cs.ClassId == assignment.ClassId)
            .Select(cs => cs.Student)
            .OrderBy(u => u.FullName)
            .ToListAsync();

        var assigned = (await _db.AssignmentStudents
            .Where(x => x.AssignmentId == assignment.Id)
            .Select(x => x.StudentId)
            .ToListAsync()).ToHashSet();

        var neverAssigned = assignment.Status == AssignmentStatus.Draft && assigned.Count == 0;

        var panel = new AssignPanelViewModel
        {
            Assignment    = assignment,
            Members       = members,
            AssignedIds   = assigned,
            Audience      = neverAssigned ? AssignmentAudience.EntireClass : assignment.Audience,
            SelectedIds   = assigned.ToHashSet(),
            LateJoin      = assignment.LateJoinPolicy == LateJoinPolicy.Manual
                                ? Assignment.SuggestedLateJoinPolicy(assignment.Type)
                                : assignment.LateJoinPolicy,
            AvailableFrom = assignment.AvailableFrom,
            Deadline      = assignment.Deadline,
            ReturnTo      = returnTo
        };

        if (TempData["AssignError"] is string error)
        {
            panel.Error = error;
            if (TempData["AssignAttempt"] is string json)
            {
                try
                {
                    var attempt = JsonSerializer.Deserialize<AssignAttemptDto>(json);
                    if (attempt is not null)
                    {
                        panel.Audience      = attempt.Audience;
                        panel.SelectedIds   = attempt.StudentIds.Concat(assigned).ToHashSet();
                        panel.LateJoin      = attempt.LateJoin == LateJoinPolicy.Manual ? panel.LateJoin : attempt.LateJoin;
                        panel.AvailableFrom = attempt.AvailableFrom;
                        panel.Deadline      = attempt.Deadline;
                    }
                }
                catch (JsonException) { /* fall back to the saved state */ }
            }
        }

        return panel;
    }

    /// <summary>Same breakdown, scoped to a single result — one student's one
    /// attempt, so a retake doesn't get pooled in with an earlier attempt.</summary>
    private static List<QuestionStatDto> BuildQuestionStats(Assignment assignment, StudentResult result)
        => BuildQuestionStats(assignment, result.StudentAnswers);

    private static List<QuestionStatDto> BuildQuestionStats(Assignment assignment, IEnumerable<StudentAnswer> answerSource)
    {
        var questions  = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();
        var allAnswers = answerSource.ToList();

        return questions.Select((q, idx) =>
        {
            var answers  = allAnswers.Where(a => a.QuestionIndex == idx).ToList();
            var correct  = answers.Count(a => a.IsCorrect == true);

            return new QuestionStatDto
            {
                Index     = idx,
                Title     = q.Title,
                Answered  = answers.Count,
                Correct   = correct,
                SolveRate = answers.Count > 0 ? (int)Math.Round(correct * 100.0 / answers.Count) : -1
            };
        }).ToList();
    }

    // GET: /Assignments/ExportGrades/id — a gradebook-ready CSV: one row per current recipient (the same statuses
    // and counts as the page), then work of students who are no longer recipients, marked as such.
    public async Task<IActionResult> ExportGrades(Guid id)
    {
        var assignment = await LoadForProgressAsync(id);
        if (assignment is null) return NotFound();

        var p  = AssignmentProgress.Build(assignment);
        var sb = new StringBuilder();
        sb.AppendLine($"Student,Email,Recipient,Status,Score (%),Submitted at ({SchoolTime.ZoneId}),Submitted attempts,In progress,Needs review,Graded by,Due ({SchoolTime.ZoneId})");

        void Row(ProgressRow r, bool current)
        {
            var latest = r.Latest;
            var status = r.State switch
            {
                SubmissionState.Submitted  => "Submitted",
                SubmissionState.InProgress => "In progress",
                _                          => "Not started"
            };
            sb.Append(Csv(r.Student.FullName)).Append(',')
              .Append(Csv(r.Student.Email)).Append(',')
              .Append(current ? "Current" : "Former").Append(',')
              .Append(status).Append(',')
              .Append(latest is null ? "" : latest.Score.ToString()).Append(',')
              .Append(latest is null ? "" : SchoolTime.Show(latest.SubmittedAt, "yyyy-MM-dd HH:mm")).Append(',')
              .Append(r.Submitted.Count).Append(',')
              .Append(r.Draft is null ? "No" : "Yes").Append(',')
              .Append(r.NeedsReview ? "Yes" : "No").Append(',')
              .Append(latest is null ? "" : latest.TeacherReviewedAt.HasValue ? "Teacher" : latest.GradingPending ? "AI (checking)" : "AI").Append(',')
              .Append(Csv(SchoolTime.Show(r.Due, "yyyy-MM-dd HH:mm", ""))).Append('\n');
        }

        foreach (var r in p.Rows) Row(r, current: true);
        foreach (var r in p.Former) Row(r, current: false);

        var fileName = $"grades-{Slug(assignment.Title)}-{DateTime.UtcNow:yyyyMMdd}.csv";
        var bytes    = new UTF8Encoding(true).GetBytes(sb.ToString()); // BOM so Excel reads UTF-8
        return File(bytes, "text/csv", fileName);
    }

    // GET: /Assignments/SubmissionDetail?resultId=...  — Teacher views a student's submission
    // GET: /Assignments/StudentStats?assignmentId=...&studentId=...
    // One student's own history for this assignment — every attempt's score,
    // plus a per-question breakdown of their most recent submitted attempt.
    // The class-wide "Question Performance" chart on Details.cshtml pools
    // every student together; this is the individual drill-down from it.
    public async Task<IActionResult> StudentStats(Guid assignmentId, Guid studentId)
    {
        var assignment = await _db.Assignments
            .Include(a => a.StudentResults.Where(r => r.StudentId == studentId))
                .ThenInclude(r => r.StudentAnswers)
            .Include(a => a.AssignmentStudents.Where(asn => asn.StudentId == studentId))
                .ThenInclude(asn => asn.Student)
            .FirstOrDefaultAsync(a => a.Id == assignmentId && (a.TeacherId == CurrentUserId || a.Class.TeacherId == CurrentUserId || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (assignment is null) return NotFound();

        var enrollment = assignment.AssignmentStudents.FirstOrDefault(asn => asn.StudentId == studentId);
        if (enrollment is null) return NotFound();

        var attempts = assignment.StudentResults
            .OrderByDescending(r => r.AttemptNumber)
            .ToList();

        var latestSubmitted = attempts.FirstOrDefault(r => r.IsSubmitted);

        return View(new StudentStatsViewModel
        {
            Assignment    = assignment,
            Student       = enrollment.Student,
            Enrollment    = enrollment,
            Attempts      = attempts,
            QuestionStats = latestSubmitted is null
                ? new()
                : BuildQuestionStats(assignment, latestSubmitted)
        });
    }

    public async Task<IActionResult> SubmissionDetail(Guid resultId, string? returnUrl = null, string? tab = null,
                                                      string? kind = null, int? question = null, int page = 1)
    {
        // Opened from a student's work page or the review queue: "Back" returns there (local paths only).
        ViewBag.ReturnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null;

        var result = await _db.StudentResults
            .Include(r => r.Assignment)
                .ThenInclude(a => a.Class)
            .Include(r => r.Student)
            .Include(r => r.StudentAnswers)
            .Include(r => r.StepResults)
            .Include(r => r.ChatSessions)
            .FirstOrDefaultAsync(r => r.Id == resultId && (r.Assignment.TeacherId == CurrentUserId || r.Assignment.Class.TeacherId == CurrentUserId || r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (result is null) return NotFound();

        // Activity: only for a teacher of this class (checked above, before anything of the attempt is read).
        if (tab == "activity")
        {
            var log = HttpContext.RequestServices.GetRequiredService<ActivityLogService>();
            // ("kind", not "action": that name is MVC's own route value.)
            ViewBag.Activity = await log.TimelineAsync(result, kind is "copy" or "paste" or "hint" or "tutor" ? kind : null, question, page);
            ViewBag.RetentionDays = log.RetentionDays;
        }
        ViewBag.Tab = tab == "activity" ? "activity" : "answers";

        // Reads only what is stored: opening the page never calls the AI.
        return View(new SubmissionDetailViewModel
        {
            Result    = result,
            Questions = JsonSerializer.Deserialize<List<QuestionDto>>(result.Assignment.Questions) ?? new(),
            AiCosts   = await AiCostReport.ForResultAsync(_db, result)
        });
    }

    // POST: /Assignments/RetryAiMarking — runs the AI marking of one photographed / typed-steps answer again when it
    // did not finish. Only the missing stage is paid for: a stored reading or grading is reused. A teacher's own mark
    // is never replaced; the result reaches the student as before (nothing is published by this).
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> RetryAiMarking(Guid resultId, int questionIndex, string? returnUrl = null)
    {
        var allowed = await _db.StudentResults.AnyAsync(r => r.Id == resultId &&
            (r.Assignment.TeacherId == CurrentUserId || r.Assignment.Class.TeacherId == CurrentUserId ||
             r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));
        if (!allowed) return NotFound();

        using var scope = _scopeFactory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<GradingService>().RegradeAnswerAsync(resultId, questionIndex);
        if (outcome.Success) TempData["Success"] = $"AI marking finished: {outcome.Marks:0.##} of {outcome.MaxMarks:0.##} marks.";
        else                 TempData["Error"]   = outcome.Error ?? "AI marking did not finish. Please try again later.";

        var back = $"/Assignments/SubmissionDetail?resultId={resultId}" +
                   (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? $"&returnUrl={Uri.EscapeDataString(returnUrl)}" : "");
        return Redirect(back + $"#q{questionIndex}");
    }

    // GET: /Assignments/LiveStatus/id — the live board polls this. The signature changes only when something a
    // teacher sees in a row changes (a hand-in, a new attempt starting, a mark, recipients); saving a draft again
    // does not, so the board never refreshes in a loop.
    [HttpGet]
    public async Task<IActionResult> LiveStatus(Guid id)
    {
        var assignment = await LoadForProgressAsync(id);
        if (assignment is null) return NotFound();

        var p = AssignmentProgress.Build(assignment);
        return Json(new
        {
            signature    = p.Signature,
            assigned     = p.Assigned,
            submitted    = p.SubmittedStudents,
            notSubmitted = p.NotSubmitted,
            attempts     = p.SubmittedAttempts,
            needsReview  = p.NeedsReviewStudents,
            total        = p.Assigned
        });
    }

    // POST: /Assignments/Publish/id  — Draft → Active
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Publish(Guid id)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return NotFound();

        try
        {
            assignment.Publish();
            await _db.SaveChangesAsync();
            TempData["Success"] = $"'{assignment.Title}' is now Active.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // POST: /Assignments/Close/id  — Active → Closed
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return NotFound();

        try
        {
            assignment.Close();
            await _db.SaveChangesAsync();
            TempData["Success"] = $"'{assignment.Title}' is now Closed.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // POST: /Assignments/Reopen/id  — Closed → Active (undo an accidental close)
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reopen(Guid id)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return NotFound();

        try
        {
            assignment.Reopen();
            await _db.SaveChangesAsync();

            TempData["Success"] = assignment.Deadline.HasValue && assignment.Deadline.Value < DateTime.UtcNow
                ? $"'{assignment.Title}' is Active again, but its deadline has passed — set a new one in Settings."
                : $"'{assignment.Title}' is Active again.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    // POST: /Assignments/Delete/id  — Soft delete
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return NotFound();

        var hasSubmissions = await HasSubmissionsAsync(id);

        try
        {
            assignment.SoftDelete(hasSubmissions);
            await _db.SaveChangesAsync();
            TempData["Success"] = "Assignment deleted.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    // POST: /Assignments/AssignStudent — Assign a single student to an assignment
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignStudent(
        Guid assignmentId, Guid studentId, DateTime? startDate, DateTime? dueDate)
    {
        var assignment = await FindOwnedAssignmentAsync(assignmentId);
        if (assignment is null) return NotFound();

        // Verify student is enrolled in the assignment's class
        var enrolled = await _db.ClassStudents
            .AnyAsync(cs => cs.ClassId == assignment.ClassId && cs.StudentId == studentId);

        if (!enrolled)
        {
            TempData["Error"] = "Student is not enrolled in this class.";
            return RedirectToAction(nameof(Details), new { id = assignmentId });
        }

        var alreadyAssigned = await _db.AssignmentStudents
            .AnyAsync(a => a.AssignmentId == assignmentId && a.StudentId == studentId);

        if (alreadyAssigned)
        {
            TempData["Error"] = "Student is already assigned to this task.";
            return RedirectToAction(nameof(Details), new { id = assignmentId });
        }

        _db.AssignmentStudents.Add(new AssignmentStudent
        {
            AssignmentId      = assignmentId,
            StudentId         = studentId,
            AssignedAt        = DateTime.UtcNow,
            StartDateOverride = SchoolTime.ToUtc(startDate),
            DueDateOverride   = SchoolTime.ToUtc(dueDate)
        });

        await _db.SaveChangesAsync();
        TempData["Success"] = "Student assigned to this task.";
        return RedirectToAction(nameof(Details), new { id = assignmentId });
    }

    // POST: /Assignments/UpdateStudentDates — sets or clears one student's custom
    // start/due dates for this assignment. Leaving both blank reverts them to the
    // assignment's own AvailableFrom/Deadline (the "Remove custom dates" action).
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStudentDates(
        Guid assignmentId, Guid studentId, DateTime? startDate, DateTime? dueDate)
    {
        var assignment = await FindOwnedAssignmentAsync(assignmentId);
        if (assignment is null) return NotFound();

        var record = await _db.AssignmentStudents
            .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId && a.StudentId == studentId);
        if (record is null) return NotFound();

        record.StartDateOverride = SchoolTime.ToUtc(startDate);
        record.DueDateOverride   = SchoolTime.ToUtc(dueDate);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Student's dates updated.";
        return RedirectToAction(nameof(Details), new { id = assignmentId });
    }

    // POST: /Assignments/UnassignStudent — Remove a single student from an assignment
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UnassignStudent(Guid assignmentId, Guid studentId)
    {
        var assignment = await FindOwnedAssignmentAsync(assignmentId);
        if (assignment is null) return NotFound();

        var record = await _db.AssignmentStudents
            .FirstOrDefaultAsync(a => a.AssignmentId == assignmentId && a.StudentId == studentId);

        if (record is null)
        {
            TempData["Error"] = "Student is not assigned to this task.";
            return RedirectToAction(nameof(Details), new { id = assignmentId });
        }

        _db.AssignmentStudents.Remove(record);
        await _db.SaveChangesAsync();

        TempData["Success"] = "Student removed from this task.";
        return RedirectToAction(nameof(Details), new { id = assignmentId });
    }

    // POST: /Assignments/AssignAllClassStudents — Bulk-assign all class students to an assignment
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssignAllClassStudents(Guid assignmentId)
    {
        var assignment = await FindOwnedAssignmentAsync(assignmentId);
        if (assignment is null) return NotFound();

        var classStudentIds = await _db.ClassStudents
            .Where(cs => cs.ClassId == assignment.ClassId)
            .Select(cs => cs.StudentId)
            .ToListAsync();

        var alreadyAssigned = await _db.AssignmentStudents
            .Where(a => a.AssignmentId == assignmentId)
            .Select(a => a.StudentId)
            .ToListAsync();

        var toAssign = classStudentIds.Except(alreadyAssigned).ToList();

        foreach (var sid in toAssign)
        {
            _db.AssignmentStudents.Add(new AssignmentStudent
            {
                AssignmentId = assignmentId,
                StudentId    = sid,
                AssignedAt   = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = $"{toAssign.Count} student(s) assigned.";
        return RedirectToAction(nameof(Details), new { id = assignmentId });
    }

    // POST: /Assignments/ImportFromPdf/{id}
    // Accepts a PDF upload, extracts text, asks GPT-4o to parse questions,
    // and returns them as JSON so the modal JS can review them.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> ImportFromPdf(Guid id, IFormFile? pdf)
    {
        if (pdf is null || pdf.Length == 0)
            return Json(new { error = "No file uploaded." });

        var ext = Path.GetExtension(pdf.FileName).ToLowerInvariant();
        if (ext != ".pdf")
            return Json(new { error = "Only PDF files are supported." });

        const long maxBytes = 20 * 1024 * 1024; // 20 MB
        if (pdf.Length > maxBytes)
            return Json(new { error = "File is too large (max 20 MB)." });

        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        try
        {
            // Read the full PDF into memory so we can try text extraction
            // and fall back to vision if the PDF is image-based.
            byte[] pdfBytes;
            await using (var stream = pdf.OpenReadStream())
            using (var ms = new MemoryStream())
            {
                await stream.CopyToAsync(ms);
                pdfBytes = ms.ToArray();
            }

            if (!FileSignature.IsPdf(pdfBytes))
                return Json(new { error = "File is not a valid PDF." });

            var parsed = await _pdfImport.ParseQuestionsFromPdfAsync(pdfBytes);

            return Json(new
            {
                questions = parsed.Questions,
                debug = new { pathUsed = parsed.PathUsed, extractedChars = parsed.ExtractedChars }
            });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
        {
            return Json(new { error = "OpenRouter API key is not configured on the server." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportFromPdf failed for assignment {AssignmentId}", id);
            return Json(new { error = "Import failed. Please try again." });
        }
    }

    // POST: /Assignments/ImportFromImage/{id}
    // Accepts an image upload (JPG, PNG, WEBP, etc.), sends it to GPT-4o vision
    // via OpenRouter, and returns parsed questions as JSON.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> ImportFromImage(Guid id, IFormFile? image)
    {
        var (imageBytes, mimeType, validationError) = await ValidateImageUploadAsync(image);
        if (validationError is not null)
            return Json(new { error = validationError });

        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        try
        {
            var questions = await _pdfImport.ParseQuestionsFromImageBytesAsync(imageBytes!, mimeType);
            return Json(new { questions });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
        {
            return Json(new { error = "OpenRouter API key is not configured on the server." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ImportFromImage failed for assignment {AssignmentId}", id);
            return Json(new { error = "Import failed. Please try again." });
        }
    }

    // POST: /Assignments/UploadQuestionImage/{id}
    // Attaches a diagram image to a question — for figures a PDF import can't carry
    // (it only extracts text/LaTeX), so the teacher attaches a screenshot/photo instead.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadQuestionImage(Guid id, string questionId, IFormFile? file)
        => await UploadQuestionAttachmentAsync(id, questionId, file, "diagram");

    // POST: /Assignments/UploadAnswerKeyImage/{id}
    // Attaches a reference solution image to a question's answer key, used by the
    // handwritten-answer grader to visually compare against a student's photo.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadAnswerKeyImage(Guid id, string questionId, IFormFile? file)
        => await UploadQuestionAttachmentAsync(id, questionId, file, "answerkey");

    private async Task<IActionResult> UploadQuestionAttachmentAsync(
        Guid id, string questionId, IFormFile? file, string kind)
    {
        if (!Guid.TryParse(questionId, out _))
            return Json(new { error = "Invalid question." });

        var (imageBytes, _, validationError) = await ValidateImageUploadAsync(file);
        if (validationError is not null)
            return Json(new { error = validationError });

        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        var ext = Path.GetExtension(file!.FileName).ToLowerInvariant();
        var path = await QuestionAttachmentStorage.SaveAsync(_env, imageBytes!, ext, id, questionId, kind);
        return Json(new { path });
    }

    /// <summary>
    /// Validates an uploaded image: allowed extension/MIME, 20 MB cap, and a
    /// magic-byte check so the file is trusted by what it actually is, not by the
    /// client-supplied filename or Content-Type. Returns the raw bytes and a
    /// normalised MIME type on success, or a user-facing error message on failure.
    /// </summary>
    private static async Task<(byte[]? bytes, string mimeType, string? error)> ValidateImageUploadAsync(IFormFile? image)
    {
        if (image is null || image.Length == 0)
            return (null, "", "No file uploaded.");

        var allowedMime = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "image/jpeg", "image/jpg", "image/png", "image/webp",
            "image/heic", "image/heif", "image/bmp", "image/gif"
        };
        var allowedExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".gif"
        };

        var ext      = Path.GetExtension(image.FileName).ToLowerInvariant();
        var mimeType = image.ContentType?.ToLowerInvariant() ?? "image/jpeg";

        if (!allowedExt.Contains(ext) && !allowedMime.Contains(mimeType))
            return (null, "", "Only image files are supported (JPG, PNG, WEBP, HEIC, etc.).");

        // Normalise MIME if the browser sent something generic
        if (!allowedMime.Contains(mimeType))
            mimeType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png"            => "image/png",
                ".webp"           => "image/webp",
                ".heic" or ".heif"=> "image/heic",
                ".bmp"            => "image/bmp",
                ".gif"            => "image/gif",
                _                 => "image/jpeg"
            };

        const long maxBytes = 20 * 1024 * 1024; // 20 MB
        if (image.Length > maxBytes)
            return (null, "", "File is too large (max 20 MB).");

        byte[] bytes;
        await using (var stream = image.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        if (!FileSignature.IsSupportedImage(bytes))
            return (null, "", "File is not a valid image.");

        return (bytes, mimeType, null);
    }

    // POST: /Assignments/GenerateQuestions/id
    // Asks the AI to generate brand-new questions from a topic description and
    // returns them as JSON so the review modal (shared with import) can show them.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> GenerateQuestions(
        Guid id, string? subject, string? topic, int? gradeLevel,
        string? difficulty, int count, string? types)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return Json(new { error = "Please describe what the questions should be about." });

        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        count      = Math.Clamp(count, 1, 20);
        difficulty = string.IsNullOrWhiteSpace(difficulty) ? "Medium" : difficulty.Trim();
        types      = string.IsNullOrWhiteSpace(types)
            ? "MultipleChoice, ShortAnswer"
            : types.Trim();

        try
        {
            var questions = await _pdfImport.GenerateQuestionsAsync(
                subject, topic.Trim(), gradeLevel, difficulty, count, types);

            if (questions.Count == 0)
                return Json(new { error = "The AI did not return any questions. Try rephrasing the topic." });

            return Json(new { questions });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
        {
            return Json(new { error = "OpenRouter API key is not configured on the server." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateQuestions failed for assignment {AssignmentId}", id);
            return Json(new { error = "Generation failed. Please try again." });
        }
    }

    // ── Background job variants: same three AI calls above, but started as a
    // background job and polled for progress instead of blocking one request
    // for the whole OpenRouter round-trip. Each *_Async_ call is still a single
    // one-shot AI request under the hood — GenerationJob's percentage is a
    // time-based estimate against the job, not a count of items actually done,
    // since the model returns its whole JSON array in one response either way.

    // POST: /Assignments/ImportFromPdfStart/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> ImportFromPdfStart(Guid id, IFormFile? pdf)
    {
        if (pdf is null || pdf.Length == 0)
            return Json(new { error = "No file uploaded." });

        var ext = Path.GetExtension(pdf.FileName).ToLowerInvariant();
        if (ext != ".pdf")
            return Json(new { error = "Only PDF files are supported." });

        const long maxBytes = 20 * 1024 * 1024; // 20 MB
        if (pdf.Length > maxBytes)
            return Json(new { error = "File is too large (max 20 MB)." });

        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        byte[] pdfBytes;
        await using (var stream = pdf.OpenReadStream())
        using (var ms = new MemoryStream())
        {
            await stream.CopyToAsync(ms);
            pdfBytes = ms.ToArray();
        }

        if (!FileSignature.IsPdf(pdfBytes))
            return Json(new { error = "File is not a valid PDF." });

        var job = _jobs.Create(TimeSpan.FromSeconds(20));
        job.OwnerId = CurrentUserId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pdfImport = scope.ServiceProvider.GetRequiredService<PdfImportService>();

                var parsed = await pdfImport.ParseQuestionsFromPdfAsync(pdfBytes);

                job.Result = parsed.Questions;
                job.Status = "done";
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
            {
                job.Error  = "OpenRouter API key is not configured on the server.";
                job.Status = "error";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImportFromPdfStart failed for assignment {AssignmentId}", id);
                job.Error  = "Import failed. Please try again.";
                job.Status = "error";
            }
        });

        return Json(new { jobId = job.Id });
    }

    // POST: /Assignments/ImportFromImageStart/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> ImportFromImageStart(Guid id, IFormFile? image)
    {
        var (imageBytes, mimeType, validationError) = await ValidateImageUploadAsync(image);
        if (validationError is not null)
            return Json(new { error = validationError });

        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        var job = _jobs.Create(TimeSpan.FromSeconds(12));
        job.OwnerId = CurrentUserId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pdfImport = scope.ServiceProvider.GetRequiredService<PdfImportService>();

                var questions = await pdfImport.ParseQuestionsFromImageBytesAsync(imageBytes!, mimeType);

                job.Result = questions;
                job.Status = "done";
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
            {
                job.Error  = "OpenRouter API key is not configured on the server.";
                job.Status = "error";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ImportFromImageStart failed for assignment {AssignmentId}", id);
                job.Error  = "Import failed. Please try again.";
                job.Status = "error";
            }
        });

        return Json(new { jobId = job.Id });
    }

    // POST: /Assignments/GenerateQuestionsStart/{id}
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> GenerateQuestionsStart(
        Guid id, string? subject, string? topic, int? gradeLevel,
        string? difficulty, int count, string? types)
    {
        if (string.IsNullOrWhiteSpace(topic))
            return Json(new { error = "Please describe what the questions should be about." });

        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null)
            return Json(new { error = "Assignment not found." });

        count      = Math.Clamp(count, 1, 20);
        difficulty = string.IsNullOrWhiteSpace(difficulty) ? "Medium" : difficulty.Trim();
        types      = string.IsNullOrWhiteSpace(types)
            ? "MultipleChoice, ShortAnswer"
            : types.Trim();
        var topicTrimmed = topic.Trim();

        // More questions take proportionally longer — scale the estimate so the
        // progress bar's pace roughly matches a 5-question vs. a 20-question ask.
        var job = _jobs.Create(TimeSpan.FromSeconds(8 + count * 1.2));
        job.OwnerId = CurrentUserId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pdfImport = scope.ServiceProvider.GetRequiredService<PdfImportService>();

                var questions = await pdfImport.GenerateQuestionsAsync(
                    subject, topicTrimmed, gradeLevel, difficulty, count, types);

                if (questions.Count == 0)
                {
                    job.Error  = "The AI did not return any questions. Try rephrasing the topic.";
                    job.Status = "error";
                    return;
                }

                job.Result = questions;
                job.Status = "done";
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("API key"))
            {
                job.Error  = "OpenRouter API key is not configured on the server.";
                job.Status = "error";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateQuestionsStart failed for assignment {AssignmentId}", id);
                job.Error  = "Generation failed. Please try again.";
                job.Status = "error";
            }
        });

        return Json(new { jobId = job.Id });
    }

    // GET: /Assignments/GenerationProgress/{jobId}
    // Polled every ~500ms by the import/generate modal while a background job
    // (started by one of the *Start actions above) is running.
    [HttpGet]
    public IActionResult GenerationProgress(Guid id)
    {
        var job = _jobs.Get(id);
        if (job is null || job.OwnerId != CurrentUserId)
            return Json(new { status = "error", percent = 100, error = "This job has expired. Please try again." });

        if (job.Status == "running")
            return Json(new { status = "running", percent = job.PercentComplete() });

        _jobs.Remove(id);

        if (job.Status == "error")
            return Json(new { status = "error", percent = 100, error = job.Error });

        return Json(new { status = "done", percent = 100, questions = job.Result });
    }

    // POST: /Assignments/ImportFromLibrary/id  — bulk-attach questions from Content Library
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ImportFromLibrary(Guid id, [FromForm] string questionIds)
    {
        // The target: an assignment this teacher owns or co-teaches whose questions may still change.
        var assignment = await FindEditableAssignmentAsync(id);
        if (assignment is null)
            return NotFound(new { error = "Assignment not found." });

        List<Guid> ids;
        try { ids = (JsonSerializer.Deserialize<List<Guid>>(questionIds ?? "") ?? new()).Distinct().ToList(); }
        catch (JsonException) { return BadRequest(new { error = "No questions selected." }); }
        if (!ids.Any())
            return Json(new { error = "No questions selected." });

        // Every source question must be one the Content Library lets this teacher use (their own, or shared with
        // all teachers). One that is not — another teacher's private question, deleted, or unknown — refuses the
        // whole batch before anything is saved, so the assignment never ends up half-imported. Missing and private
        // look the same, so the answer does not reveal that someone else's question exists.
        var questions = await _db.Questions
            .UsableBy(User.LibraryOwnerKey())
            .Where(q => ids.Contains(q.Id))
            .ToListAsync();
        if (questions.Count != ids.Count)
            return NotFound(new { error = "Some of the selected questions are not available to you. Nothing was added." });
        questions = ids.Select(i => questions.First(q => q.Id == i)).ToList();   // keep the teacher's order

        var list = JsonSerializer.Deserialize<List<JsonElement>>(assignment.Questions ?? "[]")
                   ?? new List<JsonElement>();
        // Same path as the library's own "Add to assignment": an exam part comes with its shared stem and figure.
        var added = await LibraryToAssignment.AddAsync(_env, assignment.Id, list, questions);

        assignment.Questions = JsonSerializer.Serialize(list);
        assignment.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Json(new { ok = true, added, total = list.Count });
    }

    // POST: /Assignments/ResetStudentResult — Delete a student's result to allow retake
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetStudentResult(Guid resultId, Guid assignmentId)
    {
        var result = await _db.StudentResults
            .Include(r => r.Assignment)
            .Include(r => r.Student)
            .Include(r => r.StudentAnswers)
            .Include(r => r.StepResults)
            .Include(r => r.ChatSessions)
            .FirstOrDefaultAsync(r => r.Id == resultId && (r.Assignment.TeacherId == CurrentUserId || r.Assignment.Class.TeacherId == CurrentUserId || r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (result is null)
        {
            TempData["Error"] = "Result not found.";
            return RedirectToAction(nameof(Details), new { id = assignmentId });
        }

        var studentName = result.Student.FullName;

        // Delete related entities first
        _db.StudentAnswers.RemoveRange(result.StudentAnswers);
        _db.StepResults.RemoveRange(result.StepResults);
        _db.ChatSessions.RemoveRange(result.ChatSessions);
        _db.StudentResults.Remove(result);

        await _db.SaveChangesAsync();

        TempData["Success"] = $"{studentName}'s result has been reset. They can now retake the assignment.";
        return RedirectToAction(nameof(Details), new { id = assignmentId });
    }

    // POST: /Assignments/GradeSubmission — teacher overrides AI grading for a submission
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GradeSubmission(
        Guid resultId, Guid assignmentId, string? gradesJson, decimal bonusPoints, string? returnUrl = null)
    {
        var result = await _db.StudentResults
            .Include(r => r.Assignment)
            .Include(r => r.StudentAnswers)
            .FirstOrDefaultAsync(r => r.Id == resultId && (r.Assignment.TeacherId == CurrentUserId || r.Assignment.Class.TeacherId == CurrentUserId || r.Assignment.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

        if (result is null)
        {
            TempData["Error"] = "Result not found.";
            return RedirectToAction(nameof(Details), new { id = assignmentId });
        }

        var overrides = JsonSerializer.Deserialize<List<GradeOverrideDto>>(
                            gradesJson ?? "[]",
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new();

        var allQuestions = JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>(
                               result.Assignment.Questions) ?? new();

        foreach (var ov in overrides)
        {
            var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == ov.QuestionIndex);
            if (answer is null)
            {
                // Student skipped this question — create a row so the manual grade persists.
                answer = new StudentAnswer
                {
                    StudentResultId = result.Id,
                    QuestionIndex   = ov.QuestionIndex,
                    QuestionId      = $"q{ov.QuestionIndex}"
                };
                result.StudentAnswers.Add(answer);
                _db.StudentAnswers.Add(answer);
            }

            // Marks stay within what the question is worth; extra credit goes through the bonus below.
            var maxPoints = ov.QuestionIndex >= 0 && ov.QuestionIndex < allQuestions.Count
                ? allQuestions[ov.QuestionIndex].MaxPoints : 1m;

            answer.IsCorrect         = ov.IsCorrect;
            answer.PointsAwarded     = Math.Clamp(ov.Points, 0m, maxPoints);
            answer.TeacherComment    = string.IsNullOrWhiteSpace(ov.Comment) ? null : ov.Comment.Trim();
            answer.TeacherOverridden = true;
            answer.UpdatedAt         = DateTime.UtcNow;

            // The teacher has decided — the AI's uncertainty is resolved.
            answer.NeedsReview = false;
            answer.ReviewNote  = null;
        }

        // "Bonus" — never negative (that's not what this field is for), and
        // capped well above what any real assignment would need so a typo like
        // an extra zero can't blow the score out to something absurd.
        result.BonusPoints       = Math.Clamp(bonusPoints, 0m, 1000m);
        result.TeacherReviewedAt = DateTime.UtcNow;

        // Only questions a student can answer count: a stem that other questions point to
        // through GroupId has no answer box and is excluded, exactly as when the work was first graded.
        var totalQuestions = allQuestions.Count(q =>
            string.IsNullOrEmpty(q.Id) || !allQuestions.Any(x => x.GroupId == q.Id));
        result.RecomputeScore(totalQuestions, result.Assignment.PassThreshold, GradingService.TotalMarks(allQuestions));

        await _db.SaveChangesAsync();

        TempData["Success"] = "Grades saved.";
        return RedirectToAction(nameof(SubmissionDetail), new
        {
            resultId,
            returnUrl = !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? returnUrl : null
        });
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    /// <summary>Teacher's per-question grade override sent from the submission review page.</summary>
    private class GradeOverrideDto
    {
        public int     QuestionIndex { get; set; }
        public bool    IsCorrect     { get; set; }
        public decimal Points        { get; set; }
        public string? Comment       { get; set; }
    }

    private Task<Assignment?> FindOwnedAssignmentAsync(Guid id)
        => _db.Assignments.FirstOrDefaultAsync(a => a.Id == id && (a.TeacherId == CurrentUserId || a.Class.TeacherId == CurrentUserId || a.Class.CoTeachers.Any(ct => ct.TeacherId == CurrentUserId)));

    // A student merely opening the assignment creates a draft StudentResult
    // (IsSubmitted = false) so their typed-but-unsent work survives a reload —
    // that must not freeze question editing or block deletion the way an actual
    // submission does.
    private Task<bool> HasSubmissionsAsync(Guid assignmentId)
        => _db.StudentResults.AnyAsync(r => r.AssignmentId == assignmentId && r.IsSubmitted);

    /// <summary>
    /// Loads an assignment the teacher owns, but only while its questions may
    /// still be edited. Null when it is missing or frozen by existing submissions —
    /// enforced here as well as in the UI so a stale page can't slip an edit through.
    /// </summary>
    private async Task<Assignment?> FindEditableAssignmentAsync(Guid id)
    {
        var assignment = await FindOwnedAssignmentAsync(id);
        if (assignment is null) return null;

        return assignment.AllowsQuestionEditing(await HasSubmissionsAsync(id))
            ? assignment
            : null;
    }

    /// <summary>Escapes a value for a CSV cell (RFC 4180 quoting).</summary>
    /// <summary>
    /// Escapes a field for the exported gradebook CSV. A leading '=', '+', '-',
    /// '@' or tab makes Excel/Sheets read the cell as a formula instead of text —
    /// and a student's own display name (self-registered, no character
    /// restriction) lands straight in this column, so a name like
    /// "=HYPERLINK(...)" would execute for the teacher who opens the file.
    /// Prefixing with an apostrophe is the standard mitigation: spreadsheet
    /// apps treat that as "force text" rather than running it as a formula.
    /// </summary>
    private static string Csv(string? value)
    {
        value ??= string.Empty;
        if (value.Length > 0 && "=+-@\t".IndexOf(value[0]) >= 0)
            value = "'" + value;
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    /// <summary>Produces a filesystem-safe slug from a title for use in filenames.</summary>
    private static string Slug(string title)
    {
        var chars = title.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return string.IsNullOrEmpty(slug) ? "assignment" : slug;
    }

    /// <summary>
    /// Strips HTML from the "title" field of a question JSON object so pasted
    /// rich-editor markup never reaches the stored assignment questions.
    /// </summary>
    private static string SanitizeTitleInJson(string questionJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(questionJson);
        if (node?["title"] is System.Text.Json.Nodes.JsonNode titleNode)
            node["title"] = HtmlText.StripHtml(titleNode.GetValue<string>());
        return node?.ToJsonString() ?? questionJson;
    }

    /// <summary>
    /// Parses and sanitises one question's client-supplied JSON for
    /// AddQuestion/UpdateQuestion. Malformed JSON, or a "title" that isn't a
    /// plain string, would otherwise throw straight out of those actions as an
    /// unhandled 500 — this turns that into the same kind of BadRequest the
    /// rest of the app already returns for a bad request body.
    /// </summary>
    private static bool TryParseQuestionJson(string questionJson, out JsonElement element, out string? error)
    {
        element = default;
        try
        {
            using var doc = JsonDocument.Parse(SanitizeTitleInJson(questionJson));
            element = doc.RootElement.Clone();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "That question's data is malformed and couldn't be saved.";
            return false;
        }
    }

    /// <summary>Classes the teacher can assign work to, for the form dropdowns.</summary>
    private Task<List<Class>> TeacherClassesAsync()
        => _db.Classes
            .AccessibleBy(CurrentUserId).Where(c => !c.IsArchived)
            .OrderBy(c => c.Name)
            .ToListAsync();

    private async Task PopulateClassesDropdownAsync(Guid? presetClassId = null)
    {
        var classes = await TeacherClassesAsync();
        ViewBag.Classes     = classes;
        ViewBag.PresetClass = presetClassId is { } cid ? classes.FirstOrDefault(c => c.Id == cid) : null;
    }
}
