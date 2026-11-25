using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// All grading of student work — checking a typed answer, evaluating a set of
/// solution steps (photographed or typed), and finalising a whole submission.
///
/// This logic used to live inside <c>StudentController</c>. It moved here so that
/// the slow, AI-heavy paths can also run in a background job: a job runs on its
/// own DI scope after the HTTP response has been sent, so it cannot reach into the
/// controller's request-scoped state. Keeping the work in one scoped service lets
/// both the controller (for the instant paths) and the background worker call the
/// exact same code against whichever <see cref="AppDbContext"/> their scope owns.
/// </summary>
public class GradingService
{
    private readonly AppDbContext               _db;
    private readonly AnswerGradingService       _grading;
    private readonly HandwritingAnalysisService _handwriting;
    private readonly AiMarkingService           _marking;
    private readonly IWebHostEnvironment        _env;
    private readonly ILogger<GradingService>    _logger;

    public GradingService(
        AppDbContext db,
        AnswerGradingService grading,
        HandwritingAnalysisService handwriting,
        AiMarkingService marking,
        IWebHostEnvironment env,
        ILogger<GradingService> logger)
    {
        _db          = db;
        _grading     = grading;
        _handwriting = handwriting;
        _marking     = marking;
        _env         = env;
        _logger      = logger;
    }

    private const double ConfidenceThreshold = 0.7;

    // ── Results returned to the caller (shaped into JSON by the controller) ───────

    public record TypedCheckOutcome(bool Success, string? Error, bool Correct, string? Feedback);

    /// <summary>The selected option's verdict, judged on its own and apart from the working.</summary>
    public record OptionVerdict(bool Correct, string? Feedback);

    public record StepsGradeOutcome(
        bool Success, string? Error, Guid ResultId, string? Overall,
        string? Summary, string? ExpectedAnswer, int Score, object? Steps, OptionVerdict? Option = null,
        decimal? Marks = null, decimal? MaxMarks = null, bool CanRetry = false);

    // ── Access / load ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads an Active assignment the student is assigned to and the requested
    /// question. Returns an error string when access is denied or input is invalid.
    /// </summary>
    public async Task<(Assignment? assignment, QuestionDto? question, string? error)>
        LoadAssignedQuestionAsync(Guid assignmentId, int questionIndex, Guid studentId)
    {
        var assignment = await _db.Assignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.Status == AssignmentStatus.Active);
        if (assignment is null)
            return (null, null, "Assignment not found or not active.");

        var enrollment = await _db.AssignmentStudents
            .FirstOrDefaultAsync(x => x.AssignmentId == assignmentId && x.StudentId == studentId);
        if (enrollment is null)
            return (null, null, "You are not assigned to this assignment.");

        if (IsBeforeStart(assignment, enrollment))
            return (null, null, "This assignment isn't available to you yet.");

        if (IsPastDeadline(assignment, enrollment))
            return (null, null, "This assignment deadline has passed.");

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();
        if (questionIndex < 0 || questionIndex >= questions.Count)
            return (assignment, null, "Invalid question index.");

        return (assignment, questions[questionIndex], null);
    }

    /// <summary>
    /// A student's due date is their own <see cref="AssignmentStudent.DueDateOverride"/>
    /// when a teacher gave them a custom schedule, otherwise the assignment's own
    /// <see cref="Assignment.Deadline"/>. Classroom practice can always be
    /// resubmitted freely — only Homework is deadline-gated.
    /// </summary>
    public static bool IsPastDeadline(Assignment assignment, AssignmentStudent enrollment)
    {
        var effectiveDeadline = enrollment.DueDateOverride ?? assignment.Deadline;
        return assignment.Type == AssignmentType.Homework &&
               effectiveDeadline.HasValue &&
               effectiveDeadline.Value < DateTime.UtcNow &&
               !assignment.AllowLateSubmission;
    }

    /// <summary>Mirrors <see cref="IsPastDeadline"/> for the start of the window.</summary>
    public static bool IsBeforeStart(Assignment assignment, AssignmentStudent enrollment)
    {
        var effectiveStart = enrollment.StartDateOverride ?? assignment.AvailableFrom;
        return effectiveStart.HasValue && effectiveStart.Value > DateTime.UtcNow;
    }

    // ── Instant typed-answer check (not persisted) ───────────────────────────────

    /// <summary>Grades a typed answer against the question's answer key. Null = no key, needs the AI.</summary>
    public bool? EvaluateTypedAnswer(QuestionDto q, string answerValue)
    {
        if (string.IsNullOrEmpty(q.AnswerKey) || string.IsNullOrEmpty(answerValue))
            return null;

        if (IsChoiceQuestion(q))
        {
            // Students submit option letters; a teacher may have typed the option
            // text as the key. Normalise both sides to letters before comparing,
            // order-independently (e.g. "A,C" == "C,A").
            var options = ParseOptions(q.Options);
            var student = SplitChoice(answerValue, options).Select(t => NormalizeChoiceToken(t, options)).OrderBy(s => s);
            var correct = SplitChoice(q.AnswerKey, options).Select(t => NormalizeChoiceToken(t, options)).OrderBy(s => s);
            return student.SequenceEqual(correct);
        }

        return string.Equals(
            NormalizeAnswerText(answerValue),
            NormalizeAnswerText(q.AnswerKey),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Undoes the mechanical LaTeX artefacts a math-field input adds even to plain
    /// typed text: the client wraps every math-field value in <c>$…$</c>, and
    /// MathLive itself backslash-escapes characters LaTeX treats specially — typing
    /// "25%" becomes the source "25\%". Left alone, a student's exactly-correct
    /// answer ("$25\%$") never matches a teacher's plain-text key ("25%").
    ///
    /// This is exact-text normalisation, not semantic equivalence — "1/2" reading
    /// as equal to "0.5" is still the AI grader's job (used automatically below
    /// when there's no key, or here it stays a mismatch and the teacher/AI grading
    /// pass can still catch it). This only undoes formatting the student never
    /// chose to add.
    /// </summary>
    private static string NormalizeAnswerText(string value)
    {
        var s = value.Trim();

        if (s.Length >= 2 && s[0] == '$' && s[^1] == '$')
            s = s[1..^1];

        s = s.Replace("\\%", "%").Replace("\\$", "$").Replace("\\#", "#")
             .Replace("\\&", "&").Replace("\\_", "_")
             .Replace("\\{", "{").Replace("\\}", "}");

        return s.Trim();
    }

    /// <summary>
    /// Asks the AI to judge a single typed answer that has no answer key. Used by the
    /// background job behind "Check answer"; the verdict is not persisted, the final
    /// submission is still what counts.
    /// </summary>
    public async Task<TypedCheckOutcome> AiCheckTypedAnswerAsync(QuestionDto question, int questionIndex, string answer)
    {
        try
        {
            // A choice answer is a bare letter ("B"), meaningless on its own: send the whole
            // question with its choices and the text of what was selected.
            var (questionText, answerText) = IsChoiceQuestion(question)
                ? DescribeChoiceQuestion(question, answer)
                : (question.Title, answer);

            var graded = await _grading.GradeAsync(new[]
            {
                new AnswerGradingService.ItemToGrade(questionIndex, questionText, answerText, null, question.MaxPoints)
            }, new AiCallContext("answer-check"));

            if (graded.TryGetValue(questionIndex, out var verdict))
                return new TypedCheckOutcome(true, null, verdict.Correct, verdict.Feedback);

            return new TypedCheckOutcome(false, "This question can't be checked automatically.", false, null);
        }
        catch
        {
            return new TypedCheckOutcome(false, "Couldn't check that right now. Please try again.", false, null);
        }
    }

    // ── Step grading (photographed or typed) ─────────────────────────────────────

    /// <summary>
    /// Marks a solution the student gave as numbered steps — read from a photo (and confirmed by the student) or
    /// typed — and stores the result on their in-progress attempt. For a photo the original goes to the grading
    /// model next to the reading, so a misread line is marked as written. When the AI does not finish, the work is
    /// kept as "not marked yet" (never as 0) and the stage can simply be run again: the reading is not repeated.
    /// </summary>
    public async Task<StepsGradeOutcome> EvaluateStepsAsync(
        Guid assignmentId, int questionIndex, Guid studentId, string? stepsJson, string imagePath,
        string? selectedAnswer = null)
    {
        var (assignment, question, error) = await LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (error is not null)
            return new StepsGradeOutcome(false, error, Guid.Empty, null, null, null, 0, null);

        var steps = ParseSteps(stepsJson);
        if (steps.Count == 0)
            return new StepsGradeOutcome(false, "No steps to evaluate.", Guid.Empty, null, null, null, 0, null);

        var result = await GetOrCreateSavedResultAsync(assignmentId, studentId);   // saved before the slow AI part
        var mark   = await MarkStepsAsync(result, assignment!, question!, questionIndex, steps, imagePath);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another request wrote this question's answer meanwhile (a second check of the same question, the draft
            // autosave). Read the attempt again and apply the marking to it: the reading and the grading are stored,
            // so this costs no AI call.
            _db.ChangeTracker.Clear();
            result = await GetOrCreateSavedResultAsync(assignmentId, studentId);
            mark   = await MarkStepsAsync(result, assignment!, question!, questionIndex, steps, imagePath);
            await _db.SaveChangesAsync();
        }

        if (!mark.Success)
            return new StepsGradeOutcome(false, mark.Error, result.Id, null, null, null, result.Score, steps, CanRetry: true);

        // On a choice question the selected option is judged separately from the working: by the teacher's
        // key when there is one, otherwise by the AI with the full choices. Correct working can never make a
        // wrong option right, and a working error never makes a right option wrong.
        var option = await JudgeSelectedOptionAsync(question!, questionIndex, selectedAnswer);

        return new StepsGradeOutcome(
            true, null, result.Id, mark.Overall, mark.Summary, mark.ExpectedAnswer, result.Score, mark.Steps, option,
            mark.Marks, question!.MaxPoints);
    }

    /// <summary>
    /// Runs the AI marking of one photographed or typed-steps answer again — for the teacher's "Retry AI marking" and
    /// for work handed in while the AI was unavailable. Only the unfinished stage costs anything: a stored reading or
    /// grading is reused. A teacher's own mark is never replaced.
    /// </summary>
    public async Task<StepsGradeOutcome> RegradeAnswerAsync(Guid resultId, int questionIndex)
    {
        var result = await _db.StudentResults
            .Include(r => r.StudentAnswers).Include(r => r.StepResults).Include(r => r.Assignment)
            .FirstOrDefaultAsync(r => r.Id == resultId);
        var answer = result?.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == questionIndex);
        if (result is null || answer is null) return new StepsGradeOutcome(false, "Answer not found.", Guid.Empty, null, null, null, 0, null);

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(result.Assignment.Questions) ?? new();
        if (questionIndex < 0 || questionIndex >= questions.Count)
            return new StepsGradeOutcome(false, "Invalid question index.", result.Id, null, null, null, result.Score, null);

        var steps = StepsOf(answer, result);
        if (steps.Count == 0)
            return new StepsGradeOutcome(false, "There are no steps to mark for this answer.", result.Id, null, null, null, result.Score, null);

        var mark = await MarkStepsAsync(result, result.Assignment, questions[questionIndex], questionIndex, steps, answer.ImagePath ?? "");
        await _db.SaveChangesAsync();
        return mark.Success
            ? new StepsGradeOutcome(true, null, result.Id, mark.Overall, mark.Summary, mark.ExpectedAnswer, result.Score, mark.Steps, null, mark.Marks, questions[questionIndex].MaxPoints)
            : new StepsGradeOutcome(false, mark.Error, result.Id, null, null, null, result.Score, null, CanRetry: true);
    }

    /// <summary>True for a photographed/typed-steps answer whose AI marking did not finish.</summary>
    public static bool IsUnfinished(StudentAnswer a)
        => !a.TeacherOverridden && a.GradingKey is null
           && a.GradingJson is not null && a.GradingJson.Contains("\"unfinished\":true", StringComparison.Ordinal);

    /// <summary>The student's steps kept on an answer whose marking did not finish (for the teacher's view).</summary>
    public static List<HandwritingStepDto> PendingSteps(StudentAnswer answer)
    {
        try
        {
            var stored = JsonSerializer.Deserialize<AiMarkingService.Verdict>(answer.GradingJson ?? "{}", CaseInsensitive);
            return stored?.StudentSteps?.Select(s => new HandwritingStepDto { Number = s.Number, Text = s.Text }).ToList() ?? new();
        }
        catch (JsonException) { return new(); }
    }

    private sealed record MarkOutcome(bool Success, string? Error, string? Overall, string? Summary, string? ExpectedAnswer,
                                      decimal? Marks, List<HandwritingStepDto> Steps);

    private static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };

    private static List<HandwritingStepDto> ParseSteps(string? stepsJson)
    {
        // JS posts camelCase ({number, text}) — bind case-insensitively.
        var steps = JsonSerializer.Deserialize<List<HandwritingStepDto>>(stepsJson ?? "[]", CaseInsensitive) ?? new();
        steps.RemoveAll(s => string.IsNullOrWhiteSpace(s.Text));
        return steps;
    }

    /// <summary>The student's own steps for an answer: from the stored marking, else from the step results (older work).</summary>
    private static List<HandwritingStepDto> StepsOf(StudentAnswer answer, StudentResult result)
    {
        if (!string.IsNullOrEmpty(answer.GradingJson))
        {
            try
            {
                var stored = JsonSerializer.Deserialize<AiMarkingService.Verdict>(answer.GradingJson, CaseInsensitive);
                if (stored?.StudentSteps is { Count: > 0 } own)
                    return own.Select(s => new HandwritingStepDto { Number = s.Number, Text = s.Text }).ToList();
            }
            catch (JsonException) { /* fall back to the step results */ }
        }
        return result.StepResults.Where(s => s.QuestionIndex == answer.QuestionIndex).OrderBy(s => s.StepNumber)
                     .Select(s => new HandwritingStepDto { Number = s.StepNumber, Text = s.StepText }).ToList();
    }

    private List<AiMarkingService.Page> LoadPhotos(Guid studentId, string? imagePath)
    {
        var pages = new List<AiMarkingService.Page>();
        foreach (var web in SubmissionMedia.Paths(imagePath))
        {
            var file = SubmissionStorage.FindFile(_env, studentId, Path.GetFileName(web));
            if (file is not null) pages.Add(new(System.IO.File.ReadAllBytes(file), SubmissionStorage.ContentTypeFor(file)));
        }
        return pages;
    }

    private async Task<AiMarkingService.Page?> LoadReferenceAsync(QuestionDto question)
    {
        var path = QuestionAttachmentStorage.ResolveFromWebPath(_env, question.AnswerKeyImagePath);
        return path is null ? null : new(await System.IO.File.ReadAllBytesAsync(path), SubmissionStorage.ContentTypeFor(path));
    }

    /// <summary>
    /// The marking itself: photo → reading (stored, reused) → grading with the original photo (stored, reused) →
    /// the independent check (stored with the grading) → marks on the answer. An AI failure leaves the answer
    /// marked as unfinished, with the photo and the student's steps kept.
    /// </summary>
    private async Task<MarkOutcome> MarkStepsAsync(
        StudentResult result, Assignment assignment, QuestionDto question, int questionIndex,
        List<HandwritingStepDto> steps, string imagePath)
    {
        var context = new AiCallContext("grading", null, result.StudentId, assignment.Id, result.Id, questionIndex.ToString());
        var photos  = LoadPhotos(result.StudentId, imagePath);
        var allQuestions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();

        AiMarkingService.Recognition? recognition = null;
        AiMarkingService.Verdict verdict;
        try
        {
            if (photos.Count > 0)
                recognition = await _marking.RecognizeAsync(photos, question.Title, context);

            var request = new AiMarkingService.GradeRequest(
                question.Title, question.MaxPoints, question.AnswerKey, question.MarkScheme,
                steps, photos, recognition, await LoadReferenceAsync(question));

            verdict = await _marking.GradeAsync(request, context, (v, key) =>
                _handwriting.VerifyEvaluationAsync(question.Title, question.AnswerKey, steps,
                    new HandwritingAnalysisDto { Steps = v.Steps, Overall = v.Overall, Summary = v.Summary },
                    context with { CacheKey = key }));
        }
        catch (AiCallException ex)
        {
            _logger.LogWarning("AI marking did not finish ({Kind}) for result {ResultId} q{QuestionIndex}", ex.Kind, result.Id, questionIndex);
            RecordUnfinished(result, question, questionIndex, imagePath, steps, recognition?.Key, ex.Kind);
            RecomputeResultScore(result, allQuestions, assignment.PassThreshold);
            var why = ex.Kind switch
            {
                "timeout"        => "the AI took too long",
                "bad-response"   => "the AI's answer could not be read",
                "not-configured" => "AI marking is not set up",
                _                => "the AI service was not available"
            };
            return new MarkOutcome(false, $"Marking did not finish ({why}). Your work is saved — press Try again.",
                                   null, null, null, null, steps);
        }

        verdict.StudentSteps = steps.Select(s => new AiMarkingService.RecognizedStep { Number = s.Number, Text = s.Text }).ToList();
        var marked    = AlignWithStudentSteps(verdict.Steps, steps);
        var corrected = ApplyStepCorrections(marked, verdict.Verification);
        verdict.Steps = marked;
        var points    = verdict.Points(question.MaxPoints);

        UpsertMarkedAnswer(result, question, questionIndex, imagePath, verdict, points, recognition?.Key);
        ApplyVerification(result, questionIndex, verdict.Verification, corrected);
        ApplyTranscriptionFlags(result, questionIndex, verdict);
        ReplaceStepResults(result, questionIndex, marked);
        RecomputeResultScore(result, allQuestions, assignment.PassThreshold);

        var overall = points >= question.MaxPoints ? "correct" : points > 0 ? "partial" : "incorrect";
        return new MarkOutcome(true, null, overall, verdict.Summary, verdict.ExpectedAnswer, points, marked);
    }

    private async Task<OptionVerdict?> JudgeSelectedOptionAsync(QuestionDto question, int questionIndex, string? selectedAnswer)
    {
        if (!IsChoiceQuestion(question) || string.IsNullOrWhiteSpace(selectedAnswer)) return null;

        var byKey = EvaluateTypedAnswer(question, selectedAnswer);
        if (byKey is not null) return new OptionVerdict(byKey.Value, null);

        var ai = await AiCheckTypedAnswerAsync(question, questionIndex, selectedAnswer);
        return ai.Success ? new OptionVerdict(ai.Correct, ai.Feedback) : null;
    }

    // ── Final submission ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends typed answers that couldn't be graded deterministically to the AI in
    /// one batched call: answers with no answer key, and text answers that didn't
    /// literally match the key (may still be an equivalent form). Best-effort —
    /// an AI outage leaves them ungraded rather than blocking the submission.
    /// </summary>
    public async Task GradeUngradedAnswersAsync(StudentResult result, List<QuestionDto> questions)
    {
        var items = new List<AnswerGradingService.ItemToGrade>();

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (IsGroupHeader(questions, q)) continue;

            var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == i);

            if (answer is null || answer.TeacherOverridden)        continue;
            if (!string.IsNullOrEmpty(answer.ImagePath))           continue; // photo — already AI-graded
            if (!string.IsNullOrEmpty(answer.SketchPath))          continue; // drawing — graded from the picture
            if (string.IsNullOrWhiteSpace(answer.AnswerValue))     continue; // no answer given
            if (answer.IsCorrect == true)                          continue; // already matched the key

            // Choice questions compare by letters — no AI needed.
            if (IsChoiceQuestion(q)) continue;

            items.Add(new AnswerGradingService.ItemToGrade(
                i, q.Title, answer.AnswerValue,
                string.IsNullOrWhiteSpace(q.AnswerKey) ? null : q.AnswerKey,
                q.MaxPoints, q.MarkScheme));
        }

        if (items.Count == 0) return;

        Dictionary<int, AnswerGradingService.GradedItem> graded;
        try
        {
            graded = await _grading.GradeAsync(items,
                new AiCallContext("typed-grading", null, result.StudentId, result.AssignmentId, result.Id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("AI marking of {Count} answer(s) failed for result {ResultId} ({Kind})", items.Count, result.Id,
                               (ex as AiCallException)?.Kind ?? ex.GetType().Name);
            graded = new();
        }

        foreach (var item in items)
        {
            var answer = result.StudentAnswers.First(a => a.QuestionIndex == item.Index);
            answer.UpdatedAt = DateTime.UtcNow;

            if (graded.TryGetValue(item.Index, out var verdict))
            {
                var fromCriteria = verdict.Criteria.Count > 0
                    ? new AiMarkingService.Verdict { Criteria = verdict.Criteria }.Points(item.MaxMarks)
                    : (decimal?)null;
                answer.PointsAwarded = fromCriteria ?? PointsFor(verdict.Correct, verdict.Marks, item.MaxMarks);
                answer.IsCorrect     = fromCriteria is { } p ? p >= item.MaxMarks : verdict.Correct;
                answer.AiSummary     = verdict.Feedback;
                answer.GradingJson   = JsonSerializer.Serialize(new AiMarkingService.Verdict
                {
                    Criteria = verdict.Criteria, Summary = verdict.Feedback, Model = _marking.GradingModel,
                    Overall  = answer.IsCorrect == true ? "correct" : answer.PointsAwarded > 0 ? "partial" : "incorrect"
                });
                continue;
            }

            // The AI failed or gave no verdict for this answer. The hand-in stands; the answer waits for the teacher
            // in the review queue instead of silently staying unmarked.
            answer.NeedsReview = true;
            answer.ReviewNote  = "The AI could not mark this answer. Please mark it yourself.";
        }
    }

    /// <summary>A drawn answer waiting to be marked: the saved picture and the question it answers.</summary>
    public sealed record SketchToGrade(StudentAnswer Answer, QuestionDto Question, string QuestionText, byte[] Png);

    /// <summary>
    /// Marks drawn answers from the picture with a vision model. Judging a freehand sketch is far
    /// less reliable than checking a typed answer, so the AI's mark is only a first guess: every
    /// drawing stays flagged for the teacher to confirm. An AI failure leaves the answer unmarked.
    /// </summary>
    public async Task GradeSketchesAsync(IReadOnlyList<SketchToGrade> items)
    {
        if (items.Count == 0) return;

        // The AI calls run side by side; only the results below touch the tracked entities.
        var verdicts = await Task.WhenAll(items.Select(async item =>
        {
            try
            {
                return await _handwriting.GradeSketchAsync(
                    item.QuestionText, item.Question.AnswerKey, item.Question.MaxPoints, item.Png, item.Answer.AnswerValue,
                    new AiCallContext("sketch", null, null, null, item.Answer.StudentResultId, item.Answer.QuestionIndex.ToString()));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sketch grading failed for question index {Index}", item.Answer.QuestionIndex);
                return null;
            }
        }));

        for (var i = 0; i < items.Count; i++)
        {
            var answer  = items[i].Answer;
            var verdict = verdicts[i];
            if (answer.TeacherOverridden) continue;

            answer.NeedsReview = true;
            answer.UpdatedAt   = DateTime.UtcNow;

            if (verdict is null)
            {
                answer.IsCorrect     = null;
                answer.PointsAwarded = null;
                answer.AiConfidence  = null;
                answer.ReviewNote    = "Drawn answer: the AI could not mark this drawing. Please mark it yourself.";
                continue;
            }

            answer.IsCorrect     = verdict.Correct;
            answer.PointsAwarded = PointsFor(verdict.Correct, verdict.Marks, items[i].Question.MaxPoints);
            answer.AiSummary     = verdict.Feedback;
            answer.AiConfidence  = verdict.Confidence;
            answer.ReviewNote    = "Drawn answer: the AI's mark is only a first guess. Please look at the drawing and confirm the marks.";
        }
    }

    /// <summary>
    /// Returns the student's current in-progress (not yet submitted) result for the
    /// assignment, creating one if none exists. Submitted results are left untouched.
    /// </summary>
    public async Task<StudentResult> GetOrCreateResultAsync(Guid assignmentId, Guid studentId)
    {
        var result = await _db.StudentResults
            .Include(r => r.StudentAnswers)
            .Include(r => r.StepResults)
            .Where(r => r.AssignmentId == assignmentId && r.StudentId == studentId && !r.IsSubmitted)
            .OrderByDescending(r => r.AttemptNumber)
            .FirstOrDefaultAsync();

        if (result is null)
        {
            // AttemptNumber must stay unique per (assignment, student). Based on
            // the highest attempt number that still exists, not a row count — a
            // teacher's ResetStudentResult can delete a non-latest attempt (e.g.
            // attempt 1 while attempt 3 still stands), and counting rows after
            // that would reissue an AttemptNumber that's still taken, throwing
            // a unique-constraint violation on save.
            var maxAttempt = await _db.StudentResults
                .Where(r => r.AssignmentId == assignmentId && r.StudentId == studentId)
                .Select(r => (int?)r.AttemptNumber)
                .MaxAsync() ?? 0;

            result = new StudentResult
            {
                AssignmentId  = assignmentId,
                StudentId     = studentId,
                ImagePath     = string.Empty,
                OverallStatus = ResultStatus.Incorrect,
                Score         = 0,
                AttemptNumber = maxAttempt + 1,
                SubmittedAt   = DateTime.UtcNow
            };
            _db.StudentResults.Add(result);
        }

        return result;
    }

    /// <summary>
    /// The attempt in progress, created and saved now if there is none. Used where other requests can touch the same
    /// attempt meanwhile (marking that waits seconds for the AI, the draft autosave): the attempt exists in the
    /// database before the slow part, so a second request finds it instead of creating the same attempt number.
    /// When two requests create it at the same moment, the loser takes the winner's attempt.
    /// </summary>
    public async Task<StudentResult> GetOrCreateSavedResultAsync(Guid assignmentId, Guid studentId)
    {
        var result = await GetOrCreateResultAsync(assignmentId, studentId);
        if (_db.Entry(result).State != EntityState.Added) return result;
        try
        {
            await _db.SaveChangesAsync();
            return result;
        }
        catch (DbUpdateException)
        {
            _db.Entry(result).State = EntityState.Detached;
            return await GetOrCreateResultAsync(assignmentId, studentId);
        }
    }

    /// <summary>Recomputes the aggregate score/status and refreshes the summary text.</summary>
    public void RecomputeResultScore(StudentResult result, int totalQuestions, int passThreshold)
    {
        var correctCount = result.StudentAnswers.Count(a => a.IsCorrect == true);

        result.RecomputeScore(totalQuestions, passThreshold);
        result.AiSummary = $"{correctCount} of {totalQuestions} questions correct.";
    }

    /// <summary>
    /// Recomputes the score as marks gained out of marks available, so a 4-mark question
    /// weighs four times a 1-mark one. The summary names the marks once any question is
    /// worth more than one.
    /// </summary>
    public void RecomputeResultScore(StudentResult result, List<QuestionDto> questions, int passThreshold)
    {
        var totalQuestions = CountAnswerableQuestions(questions);
        var totalMarks     = TotalMarks(questions);
        var correctCount   = result.StudentAnswers.Count(a => a.IsCorrect == true);

        result.RecomputeScore(totalQuestions, passThreshold, totalMarks);
        result.AiSummary = totalMarks == totalQuestions
            ? $"{correctCount} of {totalQuestions} questions correct."
            : $"{correctCount} of {totalQuestions} questions correct · " +
              $"{FormatMarks(result.StudentAnswers.Sum(a => a.PointsAwarded ?? 0m))} of {FormatMarks(totalMarks)} marks.";
    }

    /// <summary>Count of questions a student can actually answer — excludes group headers.</summary>
    public int CountAnswerableQuestions(List<QuestionDto> questions)
        => questions.Count(q => !IsGroupHeader(questions, q));

    /// <summary>Marks available across the questions a student can answer; a shared stem carries none.</summary>
    public static decimal TotalMarks(List<QuestionDto> questions)
        => questions.Where(q => string.IsNullOrEmpty(q.Id) || !questions.Any(x => x.GroupId == q.Id))
                    .Sum(q => q.MaxPoints);

    /// <summary>
    /// Points for an AI verdict: the marks the AI gave when it gave some, otherwise all or nothing,
    /// never outside 0..max. A verdict of "correct" always earns full marks.
    /// </summary>
    public static decimal PointsFor(bool correct, decimal? marks, decimal maxMarks)
    {
        if (correct) return maxMarks;
        return marks is { } m ? Math.Clamp(Math.Round(m * 2, MidpointRounding.AwayFromZero) / 2, 0m, maxMarks) : 0m;
    }

    private static string FormatMarks(decimal value)
        => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// A question referenced by another question's GroupId (e.g. the "Q2" stem
    /// above subpoints "2a"/"2b") is a group header, not a gradeable item — it
    /// renders with no answer input, so it must be excluded from both the
    /// per-question answer loop and the scoring denominator.
    /// </summary>
    public bool IsGroupHeader(List<QuestionDto> questions, QuestionDto q)
        => !string.IsNullOrEmpty(q.Id) && questions.Any(x => x.GroupId == q.Id);

    // ── Internal helpers ─────────────────────────────────────────────────────────

    private StudentAnswer AnswerFor(StudentResult result, QuestionDto question, int questionIndex)
    {
        var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == questionIndex)
                     // Saved meanwhile by another request (the draft autosave while the AI was marking): use that row.
                     ?? _db.StudentAnswers.FirstOrDefault(a => a.StudentResultId == result.Id && a.QuestionIndex == questionIndex);
        if (answer is null)
        {
            answer = new StudentAnswer { QuestionIndex = questionIndex, StudentResultId = result.Id };
            result.StudentAnswers.Add(answer);
            _db.StudentAnswers.Add(answer);
        }
        else if (!result.StudentAnswers.Contains(answer))
        {
            result.StudentAnswers.Add(answer);
        }
        answer.QuestionId = question.Id ?? $"q{questionIndex}";
        answer.UpdatedAt  = DateTime.UtcNow;
        return answer;
    }

    /// <summary>Stores a finished marking on the answer: marks (partial ones too), the reasons and both stage keys.</summary>
    private void UpsertMarkedAnswer(
        StudentResult result, QuestionDto question, int questionIndex, string imagePath,
        AiMarkingService.Verdict verdict, decimal points, string? recognitionKey)
    {
        var answer = AnswerFor(result, question, questionIndex);
        answer.ImagePath      = imagePath;
        answer.AiSummary      = verdict.Summary;
        answer.RecognitionKey = recognitionKey;
        answer.GradingKey     = verdict.Key;
        answer.GradingJson    = JsonSerializer.Serialize(verdict);

        // On a choice question the photo is supporting work, not the answer:
        // the selected option and its grade must stay untouched.
        if (IsChoiceQuestion(question))
        {
            answer.WorkText = string.Join("\n", verdict.Steps.Select(s => s.Text));
            return;
        }

        answer.AnswerValue = verdict.Steps.LastOrDefault()?.Text ?? string.Empty;

        // Don't clobber a teacher's manual grade when a student resubmits.
        if (!answer.TeacherOverridden)
        {
            answer.PointsAwarded = points;
            answer.IsCorrect     = points >= question.MaxPoints;
        }
    }

    /// <summary>
    /// The AI did not finish: the photo and the student's steps are kept, the answer is "not marked yet" (no marks,
    /// not 0) and waits in the teacher's review queue with a way to run the marking again.
    /// </summary>
    private void RecordUnfinished(
        StudentResult result, QuestionDto question, int questionIndex, string imagePath,
        List<HandwritingStepDto> steps, string? recognitionKey, string failure)
    {
        var answer = AnswerFor(result, question, questionIndex);
        answer.ImagePath      = imagePath;
        answer.RecognitionKey = recognitionKey;
        answer.GradingKey     = null;
        answer.GradingJson    = JsonSerializer.Serialize(new
        {
            unfinished   = true,
            failure,
            studentSteps = steps.Select(s => new { number = s.Number, text = s.Text })
        });
        if (IsChoiceQuestion(question)) answer.WorkText = string.Join("\n", steps.Select(s => s.Text));
        else                            answer.AnswerValue = steps.LastOrDefault()?.Text ?? string.Empty;

        if (answer.TeacherOverridden) return;
        if (!IsChoiceQuestion(question))
        {
            answer.IsCorrect     = null;
            answer.PointsAwarded = null;
        }
        answer.AiSummary   = null;
        answer.NeedsReview = true;
        answer.ReviewNote  = recognitionKey is null && !string.IsNullOrEmpty(imagePath)
            ? "AI marking did not finish: the photo has not been read yet. Press “Retry AI marking”, or mark it yourself."
            : "AI marking did not finish (the photo was read; the marking was not). Press “Retry AI marking”, or mark it yourself.";
    }

    /// <summary>
    /// A line whose reading decides marks and could not be read with confidence goes to the teacher, with the
    /// marker's note ("Line 3: the sign before 4 is unclear. 1 mark depends on it.").
    /// </summary>
    private static void ApplyTranscriptionFlags(StudentResult result, int questionIndex, AiMarkingService.Verdict verdict)
    {
        var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == questionIndex);
        if (answer is null || answer.TeacherOverridden) return;

        var doubtful = verdict.TranscriptionIssues.Where(i => !i.Certain && (i.MarksAffected ?? 0) > 0).ToList();
        if (!verdict.NeedsTeacher && doubtful.Count == 0) return;

        var note = !string.IsNullOrWhiteSpace(verdict.TeacherNote)
            ? verdict.TeacherNote!
            : string.Join(" ", doubtful.Select(i =>
                $"Line {i.Step}: “{i.OnPhoto ?? i.Transcribed}” is unclear. {AiMarkingService.Marks(i.MarksAffected ?? 0)} mark(s) depend on it."));
        answer.NeedsReview = true;
        answer.ReviewNote  = string.IsNullOrWhiteSpace(answer.ReviewNote) ? note : $"{note} {answer.ReviewNote}";
    }

    /// <summary>
    /// Records the verification outcome on the answer. The grade is flagged for the
    /// teacher when the verifier disagreed with it or was not sure enough.
    /// </summary>
    private static void ApplyVerification(
        StudentResult result, int questionIndex, GradingVerificationDto? verification, int correctedSteps = 0)
    {
        var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == questionIndex);
        if (answer is null) return;

        // A teacher's own grade is final — never re-flag what they already decided.
        if (answer.TeacherOverridden) return;

        if (verification is null)
        {
            answer.AiConfidence = null;
            answer.NeedsReview  = false;
            answer.ReviewNote   = null;
            return;
        }

        answer.AiConfidence = verification.Confidence;

        var unsure = !verification.Agrees || verification.Confidence < ConfidenceThreshold || correctedSteps > 0;
        answer.NeedsReview = unsure;
        answer.ReviewNote  = unsure
            ? (correctedSteps > 0 && verification.Agrees && verification.Confidence >= ConfidenceThreshold
                ? $"The second check corrected {correctedSteps} step mark(s). {verification.Notes}"
                : verification.Agrees
                    ? $"Low confidence ({verification.Confidence:0.00}). {verification.Notes}"
                    : $"The check disagreed with the first grade ({verification.Confidence:0.00}). {verification.Notes}")
            : null;
    }

    /// <summary>
    /// One entry per step the student wrote, in their order, carrying exactly the text they wrote.
    /// The model's status and explanation are matched by step number; a step it left out is
    /// reported as not checked rather than silently dropped or passed.
    /// </summary>
    private static List<HandwritingStepDto> AlignWithStudentSteps(
        List<HandwritingStepDto> modelSteps, List<HandwritingStepDto> studentSteps)
    {
        var byNumber = modelSteps.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var sameCount = modelSteps.Count == studentSteps.Count;

        return studentSteps.Select((s, i) =>
        {
            byNumber.TryGetValue(s.Number, out var m);
            if (m is null && sameCount) m = modelSteps[i];   // the model renumbered its list

            return new HandwritingStepDto
            {
                Number      = s.Number,
                Text        = s.Text,
                Status      = m?.Status ?? "incorrect",
                Explanation = m is null ? "This step could not be checked automatically." : m.Explanation
            };
        }).ToList();
    }

    /// <summary>
    /// Applies the verifier's per-step marks. Marking a step incorrect, with its reason, is applied
    /// at once; clearing a mark needs a confident verifier. Status and explanation always change
    /// together, so a red step is never left without its reason and a green one never keeps a stale
    /// one. Returns how many steps changed status.
    /// </summary>
    private static int ApplyStepCorrections(List<HandwritingStepDto> steps, GradingVerificationDto? verification)
    {
        if (verification is null || verification.Steps.Count == 0) return 0;

        var changed = 0;
        foreach (var verified in verification.Steps)
        {
            var step = steps.FirstOrDefault(s => s.Number == verified.Number);
            if (step is null) continue;

            var verifiedWrong = string.Equals(verified.Status, "incorrect", StringComparison.OrdinalIgnoreCase);
            var verifiedRight = string.Equals(verified.Status, "correct",   StringComparison.OrdinalIgnoreCase);
            var markedWrong   = string.Equals(step.Status,     "incorrect", StringComparison.OrdinalIgnoreCase);

            if (verifiedWrong && !markedWrong)
            {
                step.Status      = "incorrect";
                step.Explanation = string.IsNullOrWhiteSpace(verified.Explanation)
                    ? "This step is not correct." : verified.Explanation;
                changed++;
            }
            else if (verifiedWrong && markedWrong && string.IsNullOrWhiteSpace(step.Explanation)
                     && !string.IsNullOrWhiteSpace(verified.Explanation))
            {
                step.Explanation = verified.Explanation;     // red but unexplained: give the reason
            }
            else if (verifiedRight && markedWrong && verification.Confidence >= ConfidenceThreshold)
            {
                step.Status      = "correct";
                step.Explanation = string.Empty;
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Replaces all step results for a question with the latest evaluation.</summary>
    private void ReplaceStepResults(StudentResult result, int questionIndex, List<HandwritingStepDto> markedSteps)
    {
        var oldSteps = result.StepResults.Where(s => s.QuestionIndex == questionIndex).ToList();
        _db.StepResults.RemoveRange(oldSteps);
        foreach (var s in oldSteps) result.StepResults.Remove(s);

        foreach (var s in markedSteps)
        {
            var step = new StepResult
            {
                StudentResultId = result.Id,
                QuestionIndex   = questionIndex,
                StepNumber      = s.Number,
                StepText        = s.Text,
                Status          = string.Equals(s.Status, "correct", StringComparison.OrdinalIgnoreCase)
                                      ? ResultStatus.Correct
                                      : ResultStatus.Incorrect,
                Explanation     = s.Explanation
            };
            result.StepResults.Add(step);
            _db.StepResults.Add(step);
        }
    }

    /// <summary>True for multiple/single choice questions (answered by option letter).</summary>
    public static bool IsChoiceQuestion(QuestionDto q)
        => q.Type is "1" or "MultipleChoice" or "4" or "SingleChoice";

    /// <summary>Reads a question's options, which are stored as a JSON array.</summary>
    private static List<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(optionsJson) ?? new(); }
        catch { return new(); }
    }

    /// <summary>True for multiple/single choice questions.</summary>
    public bool IsChoice(QuestionDto q) => IsChoiceQuestion(q);

    private static readonly System.Text.RegularExpressions.Regex ChoicePrefix =
        new(@"^\(?([A-Za-z])[\).:]\s*\S", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Option text compared without markup, spacing, case or a closing full stop.</summary>
    private static bool SameOptionText(string? a, string? b)
    {
        static string Norm(string? s) =>
            System.Text.RegularExpressions.Regex.Replace((s ?? "").Replace("$", ""), @"\s+", " ")
                .Trim().TrimEnd('.').Trim().ToLowerInvariant();
        var left = Norm(a);
        return left.Length > 0 && left == Norm(b);
    }

    /// <summary>
    /// Splits a stored or submitted choice value into its selections. A value that is one option's
    /// whole text stays whole, because option text may itself contain commas.
    /// </summary>
    private static IEnumerable<string> SplitChoice(string value, List<string> options)
    {
        if (options.Any(o => SameOptionText(o, value))) return new[] { value };
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Resolves what a teacher's key or a student's selection means to its option letter (A, B, C…):
    /// a bare letter, "B) text" / "B. text", or the option's full text (ignoring $…$ and spacing).
    /// </summary>
    private static string NormalizeChoiceToken(string token, List<string> options)
    {
        token = NormalizeAnswerText(token);
        if (token.Length == 1 && char.IsLetter(token[0]))
            return token.ToUpperInvariant();

        var index = options.FindIndex(o => SameOptionText(o, token));
        if (index >= 0) return ((char)('A' + index)).ToString();

        if (ChoicePrefix.IsMatch(token))
        {
            var letterIndex = char.ToUpperInvariant(token.TrimStart('(')[0]) - 'A';
            if (letterIndex >= 0 && letterIndex < options.Count) return ((char)('A' + letterIndex)).ToString();
        }

        return token.ToUpperInvariant();
    }

    /// <summary>
    /// A choice question as the AI needs it: the stem with every option, and the text of the
    /// option(s) the student selected rather than just a letter.
    /// </summary>
    private static (string question, string answer) DescribeChoiceQuestion(QuestionDto q, string answer)
    {
        var options = ParseOptions(q.Options);
        var question = new System.Text.StringBuilder(q.Title);
        if (options.Count > 0)
        {
            question.Append("\n\nChoices:");
            for (var i = 0; i < options.Count; i++)
                question.Append($"\n{(char)('A' + i)}. {options[i]}");
        }

        var selected = SplitChoice(answer, options)
            .Select(t => NormalizeChoiceToken(t, options))
            .Distinct()
            .Select(letter =>
            {
                var i = letter.Length == 1 ? letter[0] - 'A' : -1;
                return i >= 0 && i < options.Count ? $"{letter}. {options[i]}" : letter;
            });

        return (question.ToString(), "Selected option(s): " + string.Join("; ", selected));
    }
}
