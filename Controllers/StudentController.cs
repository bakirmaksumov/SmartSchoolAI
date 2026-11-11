using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.Services;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace SmartSchoolAI.Controllers;

[Authorize(Roles = "Student")]
public class StudentController : Controller
{
    private readonly AppDbContext               _db;
    private readonly AuthService                _authService;
    private readonly IConfiguration             _config;
    private readonly IHttpClientFactory         _httpFactory;
    private readonly HandwritingAnalysisService _handwriting;
    private readonly GradingService             _grader;
    private readonly GenerationJobTracker       _jobs;
    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly IWebHostEnvironment        _env;
    private readonly ILogger<StudentController> _logger;
    private readonly TranslationService         _translator;

    public StudentController(
        AppDbContext               db,
        AuthService                authService,
        IConfiguration             config,
        IHttpClientFactory         httpFactory,
        HandwritingAnalysisService handwriting,
        GradingService             grader,
        GenerationJobTracker       jobs,
        IServiceScopeFactory       scopeFactory,
        IWebHostEnvironment        env,
        ILogger<StudentController> logger,
        TranslationService         translator)
    {
        _db           = db;
        _authService  = authService;
        _config       = config;
        _httpFactory  = httpFactory;
        _handwriting  = handwriting;
        _grader       = grader;
        _jobs         = jobs;
        _scopeFactory = scopeFactory;
        _env          = env;
        _logger       = logger;
        _translator   = translator;
    }

    private Guid CurrentStudentId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ── DASHBOARD ──────────────────────────────────────────────────────────────

    /// <summary>Main student dashboard: enrolled classes + their assigned active assignments.</summary>
    public async Task<IActionResult> Index()
    {
        var studentId = CurrentStudentId;

        var enrolledClassIds = await _db.ClassStudents
            .Where(cs => cs.StudentId == studentId)
            .Select(cs => cs.ClassId)
            .ToListAsync();

        var assignedAssignmentIds = await _db.AssignmentStudents
            .Where(asn => asn.StudentId == studentId)
            .Select(asn => asn.AssignmentId)
            .ToListAsync();

        var classes = await _db.Classes
            .Where(c => enrolledClassIds.Contains(c.Id) && !c.IsArchived)
            .Include(c => c.Teacher)
            .Include(c => c.Assignments.Where(a => a.Status == AssignmentStatus.Active
                                                && assignedAssignmentIds.Contains(a.Id)))
            .ToListAsync();

        var submittedResults = await _db.StudentResults
            .Where(r => r.StudentId == studentId && r.IsSubmitted)
            .ToListAsync();

        var latestResults = submittedResults
            .GroupBy(r => r.AssignmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).First());

        ViewBag.SubmittedResults = latestResults;

        var averageScore = latestResults.Values.Any() 
            ? (int)Math.Round(latestResults.Values.Average(r => r.Score)) 
            : 0;
        ViewBag.AverageScore = averageScore;

        var allActiveAssignments = classes
            .SelectMany(c => c.Assignments)
            .Where(a => !latestResults.ContainsKey(a.Id))
            .OrderBy(a => a.Deadline ?? DateTime.MaxValue)
            .Take(5)
            .ToList();
        ViewBag.UpcomingDeadlines = allActiveAssignments;

        return View(classes);
    }

    /// <summary>Dedicated page for student's enrolled classes overview.</summary>
    public async Task<IActionResult> Classes()
    {
        var studentId = CurrentStudentId;

        var enrolledClassIds = await _db.ClassStudents
            .Where(cs => cs.StudentId == studentId)
            .Select(cs => cs.ClassId)
            .ToListAsync();

        var assignedAssignmentIds = await _db.AssignmentStudents
            .Where(asn => asn.StudentId == studentId)
            .Select(asn => asn.AssignmentId)
            .ToListAsync();

        var classes = await _db.Classes
            .Where(c => enrolledClassIds.Contains(c.Id) && !c.IsArchived)
            .Include(c => c.Teacher)
            .Include(c => c.Assignments.Where(a => a.Status == AssignmentStatus.Active
                                                && assignedAssignmentIds.Contains(a.Id)))
            .ToListAsync();

        var submittedResults = await _db.StudentResults
            .Where(r => r.StudentId == studentId && r.IsSubmitted)
            .ToListAsync();

        var latestResults = submittedResults
            .GroupBy(r => r.AssignmentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).First());

        ViewBag.SubmittedResults = latestResults;

        return View(classes);
    }

    // ── JOIN CLASS ─────────────────────────────────────────────────────────────

    // Same abuse case as StudentRegister's invite-code check: an unthrottled guesser
    // could brute-force a 6-character code against every active class in the
    // database. "login" caps it to 5 tries/minute per IP, same as sign-in.
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> JoinClass(string inviteCode)
    {
        var (success, error) = await _authService.JoinClassAsync(CurrentStudentId, inviteCode);

        if (!success)
            TempData["Error"] = error;
        else
            TempData["Success"] = "You have successfully joined the class!";

        return RedirectToAction(nameof(Index));
    }

    // ── ASSIGNMENT LIST ────────────────────────────────────────────────────────

    /// <summary>Shows all Active assignments assigned to this student for a specific class.</summary>
    public async Task<IActionResult> Assignments(Guid classId)
    {
        var studentId = CurrentStudentId;

        // Verify enrollment
        var enrolled = await _db.ClassStudents
            .AnyAsync(cs => cs.ClassId == classId && cs.StudentId == studentId);

        if (!enrolled) return Forbid();

        var cls = await _db.Classes
            .Include(c => c.Teacher)
            .FirstOrDefaultAsync(c => c.Id == classId);

        if (cls is null) return NotFound();

        // Only show assignments this student is explicitly assigned to
        var assignedIds = await _db.AssignmentStudents
            .Where(asn => asn.StudentId == studentId)
            .Select(asn => asn.AssignmentId)
            .ToListAsync();

        var assignments = await _db.Assignments
            .Where(a => a.ClassId == classId
                     && a.Status == AssignmentStatus.Active
                     && assignedIds.Contains(a.Id))
            .OrderBy(a => a.Deadline)
            .ToListAsync();

        // Fetch submissions by this student for these assignments
        var assignmentIds = assignments.Select(a => a.Id).ToList();
        var myResults = await _db.StudentResults
            .Where(r => r.StudentId == studentId && assignmentIds.Contains(r.AssignmentId))
            .ToListAsync();

        ViewBag.Class   = cls;
        // One entry per assignment — prefer the submitted result, else the latest in-progress.
        ViewBag.Results = myResults
            .GroupBy(r => r.AssignmentId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(r => r.IsSubmitted).ThenByDescending(r => r.AttemptNumber).First());

        return View(assignments);
    }

    // ── TAKE ASSIGNMENT ────────────────────────────────────────────────────────

    /// <summary>Renders the assignment question page for a student to answer.</summary>
    public async Task<IActionResult> TakeAssignment(Guid assignmentId)
    {
        var studentId  = CurrentStudentId;
        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.Status == AssignmentStatus.Active);

        if (assignment is null) return NotFound();

        // Verify student is assigned to this specific assignment
        var enrollment = await _db.AssignmentStudents
            .FirstOrDefaultAsync(asn => asn.AssignmentId == assignmentId && asn.StudentId == studentId);

        if (enrollment is null) return Forbid();

        if (GradingService.IsBeforeStart(assignment, enrollment))
        {
            TempData["Error"] = "This assignment isn't available to you yet.";
            return RedirectToAction(nameof(Assignments), new { classId = assignment.ClassId });
        }

        if (GradingService.IsPastDeadline(assignment, enrollment))
        {
            TempData["Error"] = "This assignment deadline has passed.";
            return RedirectToAction(nameof(Assignments), new { classId = assignment.ClassId });
        }

        // Only *submitted* results count as used attempts — a student can keep
        // working (typed answers, per-question photos) until they finally submit.
        var submittedCount = await _db.StudentResults
            .CountAsync(r => r.AssignmentId == assignmentId && r.StudentId == studentId && r.IsSubmitted);

        if (!assignment.AllowsUnlimitedAttempts && submittedCount >= assignment.MaxAttempts)
        {
            TempData["Error"] = "You have used all your attempts for this assignment.";
            return RedirectToAction(nameof(AssignmentResult),
                new { assignmentId, resultId = (await _db.StudentResults
                    .Where(r => r.AssignmentId == assignmentId && r.StudentId == studentId && r.IsSubmitted)
                    .OrderByDescending(r => r.AttemptNumber)
                    .Select(r => r.Id)
                    .FirstOrDefaultAsync()) });
        }

        // Parse questions from JSON
        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions)
                        ?? new List<QuestionDto>();

        ViewBag.Assignment    = assignment;
        ViewBag.AttemptNumber = submittedCount + 1;

        return View(questions);
    }

    // ── SUBMIT ASSIGNMENT ──────────────────────────────────────────────────────

    /// <summary>Receives student answers, evaluates via AI, stores StudentResult.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> SubmitAssignment([FromForm] Dictionary<string, string> formData)
    {
        var studentId = CurrentStudentId;

        // Extract assignmentId from form data
        if (!formData.TryGetValue("assignmentId", out var assignmentIdStr) ||
            !Guid.TryParse(assignmentIdStr, out var assignmentId))
        {
            return BadRequest("Invalid assignment ID.");
        }

        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.Status == AssignmentStatus.Active);

        if (assignment is null) return NotFound();

        var isAssigned = await _db.AssignmentStudents
            .AnyAsync(asn => asn.AssignmentId == assignmentId && asn.StudentId == studentId);

        if (!isAssigned) return Forbid();

        var submittedCount = await _db.StudentResults
            .CountAsync(r => r.AssignmentId == assignmentId && r.StudentId == studentId && r.IsSubmitted);

        if (!assignment.AllowsUnlimitedAttempts && submittedCount >= assignment.MaxAttempts)
        {
            TempData["Error"] = "No attempts remaining.";
            return RedirectToAction(nameof(Assignments), new { classId = assignment.ClassId });
        }

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions)
                        ?? new List<QuestionDto>();

        // Merge typed answers into the in-progress result so per-question photo
        // solutions the student already submitted are preserved, not overwritten.
        var result = await _grader.GetOrCreateResultAsync(assignmentId, studentId);

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (_grader.IsGroupHeader(questions, q)) continue; // stem-only "Q2" above 2a/2b — no answer of its own

            var existing = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == i);

            // Keep an existing photo/handwritten answer (already graded by AI).
            if (existing != null && !string.IsNullOrEmpty(existing.ImagePath))
                continue;

            var answerValue = formData.TryGetValue($"q_{i}", out var v) ? (v ?? string.Empty) : string.Empty;
            var isCorrect   = _grader.EvaluateTypedAnswer(q, answerValue);

            if (existing is null)
            {
                existing = new StudentAnswer { StudentResultId = result.Id, QuestionIndex = i };
                result.StudentAnswers.Add(existing);
                _db.StudentAnswers.Add(existing);
            }

            // Never overwrite a teacher's manual grade.
            if (!existing.TeacherOverridden)
            {
                existing.QuestionId    = q.Id ?? $"q{i}";
                existing.AnswerValue   = answerValue;
                existing.IsCorrect     = isCorrect;
                existing.PointsAwarded = isCorrect == true ? 1m : 0m;
            }

            // "Show your work" is never auto-graded — it's kept for the teacher.
            var work = formData.TryGetValue($"work_{i}", out var w) ? w?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(work)) existing.WorkText = work;

            existing.UpdatedAt = DateTime.UtcNow;
        }

        // AI-grade the answers that exact matching couldn't decide
        // (no answer key, or a text answer written in an equivalent form).
        await _grader.GradeUngradedAnswersAsync(result, questions);

        // Finalise: mark submitted and score it. AttemptNumber was assigned when
        // the in-progress result was created (kept unique per assignment/student).
        result.IsSubmitted = true;
        result.SubmittedAt = DateTime.UtcNow;
        _grader.RecomputeResultScore(result, _grader.CountAnswerableQuestions(questions), assignment.PassThreshold);

        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(AssignmentResult), new { assignmentId, resultId = result.Id });
    }

    // ── INSTANT FEEDBACK ─────────────────────────────────────────────────────────

    /// <summary>
    /// Checks a single answer while the student is still working and returns only
    /// the verdict — the answer key never leaves the server. Nothing is persisted;
    /// the final submission is still what counts.
    ///
    /// An answer with a key is decided here and returned as <c>status:"completed"</c>
    /// straight away. An answer with no key needs the AI, so it runs as a background
    /// job (<c>status:"pending"</c> + a jobId) and the client polls <see cref="GradeProgress"/>
    /// — the wait can't time out the request, and the student plays a game meanwhile.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> CheckAnswer(Guid assignmentId, int questionIndex, string? answer)
    {
        var studentId = CurrentStudentId;
        var (assignment, question, accessError) = await _grader.LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (accessError is not null)
            return Json(new { success = false, error = accessError });

        if (!assignment!.ShowsInstantFeedback)
            return Json(new { success = false, error = "Instant feedback is turned off for this assignment." });

        if (string.IsNullOrWhiteSpace(answer))
            return Json(new { success = false, error = "Answer this question first." });

        var isCorrect = _grader.EvaluateTypedAnswer(question!, answer);

        // Has an answer key → decided instantly, no AI, no waiting.
        if (isCorrect is not null)
        {
            ActivityLog.Record(_env, "QuestionSolved", "Student", studentId,
                $"assignment={assignmentId} q={questionIndex} correct={isCorrect.Value}");
            return Json(new { success = true, status = "completed", correct = isCorrect.Value, feedback = (string?)null });
        }

        // No key → judge with the AI in the background and let the client poll.
        var job = _jobs.Create(TimeSpan.FromSeconds(8));
        job.OwnerId = studentId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var grader = scope.ServiceProvider.GetRequiredService<GradingService>();

                var outcome = await grader.AiCheckTypedAnswerAsync(question!, questionIndex, answer);
                if (!outcome.Success)
                {
                    job.Error  = outcome.Error;
                    job.Status = "error";
                    return;
                }

                job.Result = new { correct = outcome.Correct, feedback = outcome.Feedback };
                job.Status = "done";

                var jobEnv = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
                ActivityLog.Record(jobEnv, "QuestionSolved", "Student", studentId,
                    $"assignment={assignmentId} q={questionIndex} correct={outcome.Correct}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckAnswer AI grading failed for assignment {AssignmentId} q{QuestionIndex}", assignmentId, questionIndex);
                job.Error  = "Couldn't check that right now. Please try again.";
                job.Status = "error";
            }
        });

        return Json(new { success = true, status = "pending", jobId = job.Id });
    }

    // ── TRANSLATION ASSISTANCE ────────────────────────────────────────────────

    /// <summary>
    /// Translates a piece of question/feedback text for a student whose class has
    /// translation assistance turned on. Never receives or returns an answer key —
    /// the caller only ever passes question text, explanations or AI feedback.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> Translate(Guid assignmentId, string text, string languageCode)
    {
        var studentId = CurrentStudentId;

        if (string.IsNullOrWhiteSpace(text))
            return Json(new { success = false, error = "Nothing to translate." });

        var assignment = await _db.Assignments
            .Include(a => a.Class)
            .FirstOrDefaultAsync(a => a.Id == assignmentId);
        if (assignment is null) return NotFound();

        var isAssigned = await _db.AssignmentStudents
            .AnyAsync(a => a.AssignmentId == assignmentId && a.StudentId == studentId);
        if (!isAssigned) return Forbid();

        if (!assignment.Class.TranslationEnabled)
            return Json(new { success = false, error = "Translation isn't enabled for this class." });

        if (!ClassesController.SupportedLanguages.TryGetValue(languageCode, out var languageName))
            return Json(new { success = false, error = "Unsupported language." });

        var enabledLanguages = JsonSerializer.Deserialize<List<string>>(
            string.IsNullOrWhiteSpace(assignment.Class.EnabledLanguages) ? "[]" : assignment.Class.EnabledLanguages)
            ?? new List<string>();
        if (!enabledLanguages.Contains(languageCode))
            return Json(new { success = false, error = "That language isn't enabled for this class." });

        try
        {
            var translated = await _translator.TranslateAsync(text, languageName);
            return Json(new { success = true, translated });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Translation failed for assignment {AssignmentId}", assignmentId);
            return Json(new { success = false, error = "Couldn't translate that right now. Please try again." });
        }
    }

    /// <summary>
    /// Poll target for a background grading job (typed-answer check or step
    /// evaluation). Returns a running percentage until the job finishes, then the
    /// job's result once — and only for the student the job belongs to.
    /// </summary>
    [HttpGet]
    public IActionResult GradeProgress(Guid id)
    {
        var job = _jobs.Get(id);
        if (job is null || job.OwnerId != CurrentStudentId)
            return Json(new { status = "error", error = "This check has expired. Please try again." });

        if (job.Status == "running")
            return Json(new { status = "running", percent = job.PercentComplete() });

        _jobs.Remove(id);

        if (job.Status == "error")
            return Json(new { status = "error", error = job.Error ?? "Something went wrong." });

        return Json(new { status = "done", result = job.Result });
    }

    /// <summary>
    /// "Report a problem" on one question — a wrong answer key, a typo, a diagram
    /// that didn't load. Just logs the note for a teacher to read; never auto-actioned,
    /// so this stays a plain insert with no grading or AI call involved.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReportQuestionProblem(Guid assignmentId, int questionIndex, string message)
    {
        var studentId = CurrentStudentId;
        var (_, _, accessError) = await _grader.LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (accessError is not null)
            return Json(new { success = false, error = accessError });

        if (string.IsNullOrWhiteSpace(message))
            return Json(new { success = false, error = "Please describe the problem." });

        // A double click, or reopening the modal to add more detail, shouldn't
        // pile up duplicate rows for the same still-open report — update it in
        // place instead. Once a teacher has resolved one, a fresh report on the
        // same question is a new issue and gets its own row.
        var existing = await _db.QuestionReports.FirstOrDefaultAsync(r =>
            r.AssignmentId == assignmentId && r.StudentId == studentId &&
            r.QuestionIndex == questionIndex && !r.Resolved);

        if (existing is not null)
        {
            existing.Message   = message.Trim();
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _db.QuestionReports.Add(new QuestionReport
            {
                AssignmentId  = assignmentId,
                StudentId     = studentId,
                QuestionIndex = questionIndex,
                Message       = message.Trim()
            });
        }

        await _db.SaveChangesAsync();

        return Json(new { success = true });
    }

    // ── HANDWRITTEN SUBMISSION ───────────────────────────────────────────────────

    /// <summary>
    /// Step 1 of the handwritten flow: OCR only. Saves the uploaded photo and asks
    /// the vision model to transcribe each written step (no grading yet). The student
    /// reviews/corrects the transcription before submitting it for evaluation.
    /// </summary>
    private const int MaxHandwrittenPages = 5;

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> AnalyzeHandwritten(Guid assignmentId, int questionIndex, List<IFormFile>? images)
    {
        if (images is null || images.Count == 0)
            return Json(new { success = false, error = "No image uploaded." });

        if (images.Count > MaxHandwrittenPages)
            return Json(new { success = false, error = $"Please upload at most {MaxHandwrittenPages} photos." });

        var studentId = CurrentStudentId;
        var (_, question, accessError) = await _grader.LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (accessError is not null)
            return Json(new { success = false, error = accessError });

        try
        {
            var pages = new List<(byte[] bytes, string mime)>();
            var paths = new List<string>();

            foreach (var image in images)
            {
                if (image.Length == 0) continue;

                if (!IsValidImage(image, out var mimeType, out var validationError))
                    return Json(new { success = false, error = validationError });

                byte[] bytes;
                await using (var stream = image.OpenReadStream())
                using (var ms = new MemoryStream())
                {
                    await stream.CopyToAsync(ms);
                    bytes = ms.ToArray();
                }

                if (!FileSignature.IsSupportedImage(bytes))
                    return Json(new { success = false, error = "File is not a valid image." });

                var ext = Path.GetExtension(image.FileName).ToLowerInvariant();
                paths.Add(await SaveImageBytesAsync(bytes, ext, studentId));
                pages.Add((bytes, mimeType));
            }

            if (pages.Count == 0)
                return Json(new { success = false, error = "No valid images uploaded." });

            var analysis = await _handwriting.ExtractStepsAsync(pages, question!.Title);

            return Json(new
            {
                success         = true,
                imagePaths      = paths,
                detectedProblem = analysis.DetectedProblem,
                steps           = analysis.Steps
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AnalyzeHandwritten failed for assignment {AssignmentId} q{QuestionIndex}", assignmentId, questionIndex);
            return Json(new { success = false, error = "Analysis failed. Please try again." });
        }
    }

    /// <summary>
    /// Step 2 of the handwritten flow: evaluation. Grades the student's corrected
    /// steps against the question and persists the result. The grading is several
    /// AI calls (grade + a second-opinion pass), so it runs as a background job and
    /// the client polls <see cref="GradeProgress"/> while the student plays a game.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> SubmitHandwritten(
        Guid assignmentId, int questionIndex, string imagePathsJson, [FromForm] string stepsJson)
    {
        var studentId = CurrentStudentId;
        var (_, _, accessError) = await _grader.LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (accessError is not null)
            return Json(new { success = false, error = accessError });

        // Guard against path traversal: every image must belong to this student.
        var imagePaths     = JsonSerializer.Deserialize<List<string>>(imagePathsJson ?? "[]") ?? new();
        var expectedPrefix = $"{SubmissionStorage.WebPrefix}/{studentId}/";
        if (imagePaths.Count == 0 ||
            imagePaths.Any(p => string.IsNullOrEmpty(p) || !p.StartsWith(expectedPrefix, StringComparison.Ordinal)))
            return Json(new { success = false, error = "Invalid image reference." });

        return EnqueueStepGrading(assignmentId, questionIndex, studentId, stepsJson, JsonSerializer.Serialize(imagePaths));
    }

    /// <summary>
    /// Typed equivalent of <see cref="SubmitHandwritten"/>: the student writes the
    /// solution as numbered steps directly (the "Type solution" tab) instead of
    /// photographing it, so there is no OCR pass and no photo to record — the typed
    /// steps go straight to the same background grading used for the handwritten path.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> SubmitTypedSteps(
        Guid assignmentId, int questionIndex, [FromForm] string stepsJson)
    {
        var studentId = CurrentStudentId;
        var (_, _, accessError) = await _grader.LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (accessError is not null)
            return Json(new { success = false, error = accessError });

        return EnqueueStepGrading(assignmentId, questionIndex, studentId, stepsJson, imagePath: string.Empty);
    }

    /// <summary>
    /// Queues step grading to run in the background on its own DI scope and returns a
    /// jobId for the client to poll. Access is already validated by the caller; the
    /// grading service re-loads and re-checks in its own scope. Shared by the
    /// handwritten (with photo path) and typed (empty path) submission actions.
    /// </summary>
    private IActionResult EnqueueStepGrading(
        Guid assignmentId, int questionIndex, Guid studentId, string? stepsJson, string imagePath)
    {
        var job = _jobs.Create(TimeSpan.FromSeconds(25));
        job.OwnerId = studentId;

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var grader = scope.ServiceProvider.GetRequiredService<GradingService>();

                var outcome = await grader.EvaluateStepsAsync(assignmentId, questionIndex, studentId, stepsJson, imagePath);
                if (!outcome.Success)
                {
                    job.Error  = outcome.Error;
                    job.Status = "error";
                    return;
                }

                job.Result = new
                {
                    resultId       = outcome.ResultId,
                    overall        = outcome.Overall,
                    summary        = outcome.Summary,
                    expectedAnswer = outcome.ExpectedAnswer,
                    score          = outcome.Score,
                    steps          = outcome.Steps
                };
                job.Status = "done";

                var jobEnv = scope.ServiceProvider.GetRequiredService<IWebHostEnvironment>();
                ActivityLog.Record(jobEnv, "QuestionSolved", "Student", studentId,
                    $"assignment={assignmentId} q={questionIndex} correct={string.Equals(outcome.Overall, "correct", StringComparison.OrdinalIgnoreCase)}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Step grading failed for assignment {AssignmentId} q{QuestionIndex}", assignmentId, questionIndex);
                job.Error  = "Evaluation failed. Please try again.";
                job.Status = "error";
            }
        });

        return Json(new { success = true, status = "pending", jobId = job.Id });
    }

    // ── RESULT ─────────────────────────────────────────────────────────────────

    /// <summary>Shows the AI-evaluated result for a submission.</summary>
    public async Task<IActionResult> AssignmentResult(Guid assignmentId, Guid resultId)
    {
        var studentId = CurrentStudentId;

        var result = await _db.StudentResults
            .Include(r => r.Assignment)
                .ThenInclude(a => a.Class)
            .Include(r => r.StepResults)
            .Include(r => r.StudentAnswers)
            .FirstOrDefaultAsync(r => r.Id == resultId && r.StudentId == studentId);

        if (result is null) return NotFound();

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(result.Assignment.Questions)
                        ?? new List<QuestionDto>();

        ViewBag.Questions = questions;

        return View(result);
    }

    // ── AI TUTOR CHAT ──────────────────────────────────────────────────────────

    /// <summary>Starts or continues an AI chat session for a given result.</summary>
    public async Task<IActionResult> Chat(Guid resultId, string mode = "hint")
    {
        var studentId = CurrentStudentId;

        var result = await _db.StudentResults
            .Include(r => r.Assignment)
            .Include(r => r.ChatSessions)
            .FirstOrDefaultAsync(r => r.Id == resultId && r.StudentId == studentId);

        if (result is null) return NotFound();

        var session = result.ChatSessions
            .FirstOrDefault(c => c.Mode == mode);

        if (session is null)
        {
            session = new ChatSession
            {
                StudentResultId = resultId,
                Mode            = mode,
                Messages        = "[]"
            };
            _db.ChatSessions.Add(session);
            await _db.SaveChangesAsync();
        }

        ViewBag.Result = result;
        ViewBag.Mode   = mode;

        return View(session);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task<IActionResult> SendChatMessage(Guid sessionId, string userMessage)
    {
        var studentId = CurrentStudentId;

        var session = await _db.ChatSessions
            .Include(c => c.StudentResult)
                .ThenInclude(r => r.Assignment)
            .Include(c => c.StudentResult)
                .ThenInclude(r => r.StudentAnswers)
            .FirstOrDefaultAsync(c => c.Id == sessionId && c.StudentResult.StudentId == studentId);

        if (session is null) return NotFound();

        var messages = JsonSerializer.Deserialize<List<ChatMessageDto>>(session.Messages)
                       ?? new List<ChatMessageDto>();

        // Escalate hints based on how many replies the tutor has already given.
        var priorAssistantTurns = messages.Count(m => m.Role == "assistant");

        messages.Add(new ChatMessageDto { Role = "user", Content = userMessage });

        var assignment    = session.StudentResult.Assignment;
        var systemPrompt  = BuildTutorSystemPrompt(session.Mode, priorAssistantTurns, assignment.AllowSolutionReveal);
        var context       = BuildMistakeContext(session.StudentResult, assignment);

        var chatMessages = new List<object> { new { role = "system", content = systemPrompt } };
        if (!string.IsNullOrEmpty(context))
            chatMessages.Add(new { role = "system", content = context });
        // The full conversation is kept in session.Messages for the student to scroll
        // back through — only what's sent to the model is capped.
        foreach (var m in TrimHistory(messages))
            chatMessages.Add(new { role = m.Role, content = m.Content });

        string reply;
        try
        {
            reply = await CallTutorAsync(chatMessages);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SendChatMessage failed for session {SessionId}", sessionId);
            reply = "Sorry, I couldn't reach the tutor right now. Please try again.";
        }

        messages.Add(new ChatMessageDto { Role = "assistant", Content = reply });

        session.Messages  = JsonSerializer.Serialize(messages);
        session.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return RedirectToAction(nameof(Chat),
            new { resultId = session.StudentResultId, mode = session.Mode });
    }

    // ── TUTOR HELPERS (shared by SendChatMessage + ChatStream) ──────────────────

    /// <summary>
    /// How many of the most recent exchanges (one exchange = one user + one
    /// assistant message) get replayed to the AI. The fixed system prompt and
    /// mistake/question context are resent every turn regardless — those are
    /// small and constant-sized — but the growing back-and-forth is the part that
    /// scales with conversation length. A hint/explain tutor rarely needs more
    /// than the last few turns to stay coherent, so capping it here keeps token
    /// cost roughly flat instead of growing with every reply in a long chat.
    /// </summary>
    private const int MaxHistoryExchanges = 4;

    /// <summary>Caps the model's reply length — the prompts already ask for 2-5 sentences; this enforces it.</summary>
    private const int TutorMaxReplyTokens = 500;

    /// <summary>Keeps only the most recent messages, trimmed to whole exchanges where possible.</summary>
    private static List<ChatMessageDto> TrimHistory(List<ChatMessageDto> messages)
    {
        var limit = MaxHistoryExchanges * 2;
        return messages.Count <= limit ? messages : messages.Skip(messages.Count - limit).ToList();
    }

    /// <summary>
    /// Resolves the tutor's model. Hints and mistake explanations are a lighter task
    /// than rigorous answer grading, so the tutor gets its own — cheaper — default
    /// (the same model already trusted elsewhere in the app for PDF import) rather
    /// than sharing the pricier grading model. Still overridable via config.
    /// </summary>
    private string TutorModel()
        => _config["OpenRouter:TutorModel"] ?? "google/gemini-2.5-flash";

    /// <summary>
    /// Builds the tutor's system prompt for a mode. Hint mode escalates with the
    /// number of replies already given, and full answers are only revealed when
    /// the teacher enabled <see cref="Assignment.AllowSolutionReveal"/>.
    /// </summary>
    private static string BuildTutorSystemPrompt(string mode, int priorAssistantTurns, bool allowReveal)
    {
        const string latex = "Use LaTeX with $ delimiters for any math.";

        return mode switch
        {
            "hint" => priorAssistantTurns switch
            {
                0 => "You are an encouraging AI tutor for school students. Give ONE gentle hint in 2-3 sentences " +
                     "and ask a single guiding question. Do NOT reveal the answer or do the final calculation. " + latex,
                1 => "You are an AI tutor. The student needs more help. Point to the specific step or method they " +
                     "should use, still WITHOUT giving the final answer, and ask one focused question. " + latex,
                _ => "You are an AI tutor. The student is stuck after several hints. Walk through the reasoning " +
                     "step by step" + (allowReveal
                         ? ", and you may state the final answer if it helps. "
                         : ", but stop before the final step so the student finishes it themselves. Do NOT state the final answer. ") + latex
            },
            // The context message (BuildMistakeContext) lists exactly which questions the
            // student got wrong, what they answered, and the AI feedback already given —
            // this must be about THAT, not a generic concept refresher with invented
            // numbers the student never saw.
            "explain" or "concept" => "You are an AI teacher reviewing a graded assignment with the student. The system " +
                         "message below lists which question(s) they got wrong, what they answered, and why. Address " +
                         "THOSE specific mistakes directly — quote or refer to their actual answer, explain where the " +
                         "reasoning went wrong, and correct the misconception. Do not invent an unrelated example " +
                         "instead of discussing their real answer. Do not simply state the correct final answer unless " +
                         "the context explicitly allows revealing it. Keep it to 3-5 sentences. " + latex,
            "solution" => allowReveal
                ? "You are an AI tutor. Show the complete step-by-step solution, numbering each step. Be structured " +
                  "and thorough. " + latex
                : "You are an AI tutor. Full solutions are turned off for this assignment by the teacher. Give the " +
                  "most detailed guidance you can WITHOUT stating the final answer, and encourage the student to " +
                  "finish the last step. " + latex,
            _ => "You are a helpful AI tutor for school students. Answer in 2-4 sentences without giving the final " +
                 "answer directly. " + latex
        };
    }

    /// <summary>Builds a short context string describing the assignment's questions.</summary>
    private static string BuildQuestionContext(Assignment assignment)
    {
        var context = $"Assignment: {assignment.Title}. ";
        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();
        if (questions.Count > 0)
            context += "Questions available: " +
                string.Join("; ", questions.Select((q, i) => $"Q{i + 1}: {q.Title}"));
        return context;
    }

    /// <summary>
    /// Builds context for the post-submission tutor (<see cref="Chat"/>/<see cref="SendChatMessage"/>):
    /// exactly which questions the student got wrong, what they answered, the
    /// correct answer when the question has one, and the feedback already given
    /// during grading. Without this, "explain my mistake" has nothing to point
    /// at — the tutor sees the assignment's topic list but not the student's own
    /// work, which is why it fell back to inventing an unrelated example instead.
    /// </summary>
    /// <summary>Caps how many wrong questions go into the tutor's context — a handful is plenty to work from.</summary>
    private const int MaxMistakesInContext = 6;

    /// <summary>Caps one question's grading-feedback text so a verbose AI summary can't dominate the context.</summary>
    private const int MaxFeedbackChars = 200;

    private static string BuildMistakeContext(StudentResult result, Assignment assignment)
    {
        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();

        var wrongAnswers = result.StudentAnswers
            .Where(a => a.IsCorrect == false && a.QuestionIndex >= 0 && a.QuestionIndex < questions.Count)
            .OrderBy(a => a.QuestionIndex)
            .ToList();

        var wrong = wrongAnswers
            .Take(MaxMistakesInContext)
            .Select(a =>
            {
                var q = questions[a.QuestionIndex];
                var line = $"Q{a.QuestionIndex + 1}: \"{q.Title}\". Student answered: \"{a.AnswerValue}\".";
                var correctText = ResolveAnswerKeyText(q);
                if (!string.IsNullOrWhiteSpace(correctText))
                    line += $" Correct answer: \"{correctText}\".";
                if (!string.IsNullOrWhiteSpace(a.AiSummary))
                    line += $" Grading feedback: {Truncate(a.AiSummary, MaxFeedbackChars)}";
                return line;
            })
            .ToList();

        var header = $"Assignment: {assignment.Title}. Score: {result.Score}%.";

        if (wrong.Count == 0)
            return header + " The student answered every graded question correctly — there is no mistake to " +
                   "explain here. If asked to explain a mistake, say there wasn't one and offer to explain a " +
                   "concept from the assignment instead.";

        var extra = wrongAnswers.Count - wrong.Count;
        var footer = extra > 0 ? $"\n(and {extra} more wrong answer(s) not shown here)" : "";

        return header + " Questions the student got wrong:\n" + string.Join("\n", wrong) + footer;
    }

    private static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "…";

    /// <summary>
    /// A choice question's AnswerKey is a bare option letter ("B"), meaningless to
    /// the tutor without the option list — this resolves it to the option's actual
    /// text. Any other question type's key is already human-readable as-is.
    /// </summary>
    private static string ResolveAnswerKeyText(QuestionDto q)
    {
        if (string.IsNullOrWhiteSpace(q.AnswerKey)) return "";

        if (q.AnswerKey.Length == 1 && char.IsLetter(q.AnswerKey[0]) && !string.IsNullOrWhiteSpace(q.Options))
        {
            try
            {
                var options = JsonSerializer.Deserialize<List<string>>(q.Options) ?? new();
                var index   = char.ToUpperInvariant(q.AnswerKey[0]) - 'A';
                if (index >= 0 && index < options.Count)
                    return options[index];
            }
            catch (JsonException) { /* fall through to the raw key */ }
        }

        return q.AnswerKey;
    }

    /// <summary>Calls OpenRouter for a non-streaming tutor completion and returns the reply text.</summary>
    private async Task<string> CallTutorAsync(List<object> chatMessages)
    {
        var apiKey  = _config["OpenRouter:ApiKey"]  ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        var model   = TutorModel();

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("OpenRouter API key is not configured.");

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var payload = new { model, temperature = 0.5, max_tokens = TutorMaxReplyTokens, messages = chatMessages };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new Exception($"AI service returned {(int)response.StatusCode}.");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
                  .GetProperty("choices")[0]
                  .GetProperty("message")
                  .GetProperty("content")
                  .GetString() ?? "";
    }

    // ── CHAT STREAM (SSE) ──────────────────────────────────────────────────────

    /// <summary>
    /// Server-Sent Events endpoint for real-time AI tutor streaming.
    /// Proxies to OpenRouter API using config from appsettings.json.
    ///
    /// The controller is Student-only, but a teacher previewing their own
    /// assignment needs to be able to try the tutor too — [AllowAnonymous] opts
    /// this one action out of that class-level restriction, and the role +
    /// ownership check below stands in for it manually.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("ai")]
    public async Task ChatStream([FromBody] TutorChatRequest request)
    {
        if (User.Identity?.IsAuthenticated != true ||
            !(User.IsInRole(Role.Student) || User.IsInRole(Role.Teacher)))
        {
            Response.StatusCode = 401;
            return;
        }

        Response.ContentType                  = "text/event-stream";
        Response.Headers["Cache-Control"]     = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var apiKey  = _config["OpenRouter:ApiKey"]  ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        var model   = TutorModel();

        // Load the assignment for context ONLY if the caller is allowed to see it:
        // a student assigned to it, or the teacher who owns it (Preview mode) —
        // so a student can't pull another assignment's questions into the tutor.
        Assignment? assignment = null;
        if (!string.IsNullOrEmpty(request.AssignmentId) && Guid.TryParse(request.AssignmentId, out var aId))
        {
            var currentUserId = CurrentStudentId;
            var isAuthorized = User.IsInRole(Role.Teacher)
                ? await _db.Assignments.AnyAsync(a => a.Id == aId
                    && (a.TeacherId == currentUserId
                        || a.Class.TeacherId == currentUserId
                        || a.Class.CoTeachers.Any(ct => ct.TeacherId == currentUserId)))
                : await _db.AssignmentStudents.AnyAsync(x => x.AssignmentId == aId && x.StudentId == currentUserId);

            if (isAuthorized)
                assignment = await _db.Assignments.FindAsync(aId);
        }

        var priorAssistantTurns = request.History?.Count(h => h.Role == "assistant") ?? 0;
        var allowReveal         = assignment?.AllowSolutionReveal ?? false;
        var systemPrompt        = BuildTutorSystemPrompt(request.Mode ?? "hint", priorAssistantTurns, allowReveal);
        var questionContext     = assignment is null ? "" : BuildQuestionContext(assignment);

        // Assemble messages
        var messages = new List<object>
        {
            new { role = "system", content = systemPrompt }
        };

        if (!string.IsNullOrEmpty(questionContext))
            messages.Add(new { role = "system", content = questionContext });

        if (request.History != null)
        {
            foreach (var h in TrimHistory(request.History))
                messages.Add(new { role = h.Role, content = h.Content });
        }

        messages.Add(new { role = "user", content = request.Message });

        var body = new
        {
            model,
            stream     = true,
            max_tokens = TutorMaxReplyTokens,
            messages
        };

        try
        {
            using var httpClient  = _httpFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            httpRequest.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var httpResponse = await httpClient.SendAsync(
                httpRequest, HttpCompletionOption.ResponseHeadersRead);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync();
                _logger.LogError(
                    "ChatStream: OpenRouter returned {Status}: {Body}", httpResponse.StatusCode, errorBody);

                await WriteSseAsync("⚠️ Sorry, I couldn't reach the tutor right now. Please try again.");
                return;
            }

            using var stream = await httpResponse.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

                var data = line["data: ".Length..];
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var deltaContent = doc.RootElement
                        .GetProperty("choices")[0]
                        .GetProperty("delta")
                        .GetProperty("content")
                        .GetString();

                    if (!string.IsNullOrEmpty(deltaContent))
                        await WriteSseAsync(deltaContent);
                }
                catch { /* skip malformed chunk */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatStream failed");
            await WriteSseAsync("⚠️ Sorry, I couldn't reach the tutor right now. Please try again.");
        }
    }

    /// <summary>
    /// Writes one text chunk as an SSE event. The chunk is JSON-encoded so it travels as a
    /// single line: a raw chunk containing a newline would break the "data: " framing (the
    /// lines after the first are dropped by the client, losing text and paragraph breaks),
    /// and leading or trailing whitespace reaches the browser exactly as the model produced it.
    /// </summary>
    private async Task WriteSseAsync(string chunk)
    {
        await Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk)}\n\n");
        await Response.Body.FlushAsync();
    }

    // ── HANDWRITTEN HELPERS ──────────────────────────────────────────────────────

    private static readonly HashSet<string> AllowedImageExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".heic", ".heif", ".bmp", ".gif"
    };

    /// <summary>Validates the uploaded image and resolves a usable MIME type.</summary>
    private static bool IsValidImage(IFormFile image, out string mimeType, out string error)
    {
        error    = string.Empty;
        mimeType = image.ContentType?.ToLowerInvariant() ?? "image/jpeg";

        var ext = Path.GetExtension(image.FileName).ToLowerInvariant();
        if (!AllowedImageExt.Contains(ext))
        {
            error = "Only image files are supported (JPG, PNG, WEBP, HEIC, etc.).";
            return false;
        }

        const long maxBytes = 20 * 1024 * 1024; // 20 MB
        if (image.Length > maxBytes)
        {
            error = "File is too large (max 20 MB).";
            return false;
        }

        // Normalise MIME if the browser sent something generic.
        if (!mimeType.StartsWith("image/", StringComparison.Ordinal))
            mimeType = ext switch
            {
                ".png"             => "image/png",
                ".webp"            => "image/webp",
                ".heic" or ".heif" => "image/heic",
                ".bmp"             => "image/bmp",
                ".gif"             => "image/gif",
                _                  => "image/jpeg"
            };

        return true;
    }

    /// <summary>Saves submission image bytes to protected storage and returns its web path.</summary>
    private Task<string> SaveImageBytesAsync(byte[] bytes, string ext, Guid studentId)
        => SubmissionStorage.SaveAsync(_env, bytes, ext, studentId);

}
