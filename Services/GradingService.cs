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
    private readonly IWebHostEnvironment        _env;
    private readonly ILogger<GradingService>    _logger;

    public GradingService(
        AppDbContext db,
        AnswerGradingService grading,
        HandwritingAnalysisService handwriting,
        IWebHostEnvironment env,
        ILogger<GradingService> logger)
    {
        _db          = db;
        _grading     = grading;
        _handwriting = handwriting;
        _env         = env;
        _logger      = logger;
    }

    private const double ConfidenceThreshold = 0.7;

    // ── Results returned to the caller (shaped into JSON by the controller) ───────

    public record TypedCheckOutcome(bool Success, string? Error, bool Correct, string? Feedback);

    public record StepsGradeOutcome(
        bool Success, string? Error, Guid ResultId, string? Overall,
        string? Summary, string? ExpectedAnswer, int Score, object? Steps);

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
            var student = answerValue.Split(',').Select(t => NormalizeChoiceToken(t, options)).OrderBy(s => s);
            var correct = q.AnswerKey.Split(',').Select(t => NormalizeChoiceToken(t, options)).OrderBy(s => s);
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
            var graded = await _grading.GradeAsync(new[]
            {
                new AnswerGradingService.ItemToGrade(questionIndex, question.Title, answer, null)
            });

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
    /// Grades a set of numbered steps against the question, gets a second opinion,
    /// and persists both onto the student's in-progress result. Shared by the
    /// handwritten (OCR) and typed-steps paths; they differ only in whether there is
    /// an <paramref name="imagePath"/> to record.
    /// </summary>
    public async Task<StepsGradeOutcome> EvaluateStepsAsync(
        Guid assignmentId, int questionIndex, Guid studentId, string? stepsJson, string imagePath)
    {
        var (assignment, question, error) = await LoadAssignedQuestionAsync(assignmentId, questionIndex, studentId);
        if (error is not null)
            return new StepsGradeOutcome(false, error, Guid.Empty, null, null, null, 0, null);

        // JS posts camelCase ({number, text}) — bind case-insensitively.
        var steps = JsonSerializer.Deserialize<List<HandwritingStepDto>>(
                        stepsJson ?? "[]",
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new();

        steps.RemoveAll(s => string.IsNullOrWhiteSpace(s.Text));
        if (steps.Count == 0)
            return new StepsGradeOutcome(false, "No steps to evaluate.", Guid.Empty, null, null, null, 0, null);

        (byte[] bytes, string mime)? answerKeyImage = null;
        var answerKeyImagePath = QuestionAttachmentStorage.ResolveFromWebPath(_env, question!.AnswerKeyImagePath);
        if (answerKeyImagePath is not null)
        {
            answerKeyImage = (
                await System.IO.File.ReadAllBytesAsync(answerKeyImagePath),
                SubmissionStorage.ContentTypeFor(answerKeyImagePath));
        }

        HandwritingAnalysisDto analysis;
        try
        {
            analysis = await _handwriting.EvaluateStepsAsync(
                question.Title, question.AnswerKey, steps, answerKeyImage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EvaluateStepsAsync failed for assignment {AssignmentId} q{QuestionIndex}", assignmentId, questionIndex);
            return new StepsGradeOutcome(false, "Evaluation failed. Please try again.", Guid.Empty, null, null, null, 0, null);
        }

        // Second opinion: an independent pass re-solves the task and checks the grade.
        // A single grading call can mark a correct solution wrong, so a disagreement
        // or a low-confidence verdict is escalated to the teacher instead of standing.
        var verification = await _handwriting.VerifyEvaluationAsync(
            question.Title, question.AnswerKey, steps, analysis);

        if (verification is not null && !verification.Agrees &&
            !string.IsNullOrWhiteSpace(verification.Overall))
        {
            analysis.Overall = verification.Overall;   // the more rigorous pass wins
        }

        var result          = await GetOrCreateResultAsync(assignmentId, studentId);
        var questionCorrect = string.Equals(analysis.Overall, "correct", StringComparison.OrdinalIgnoreCase);

        UpsertQuestionAnswer(result, question, questionIndex, imagePath, analysis, questionCorrect);
        ApplyVerification(result, questionIndex, verification);
        ReplaceStepResults(result, questionIndex, analysis);

        var allQuestions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment!.Questions) ?? new();
        RecomputeResultScore(result, CountAnswerableQuestions(allQuestions), assignment.PassThreshold);

        await _db.SaveChangesAsync();

        return new StepsGradeOutcome(
            true, null, result.Id, analysis.Overall, analysis.Summary,
            analysis.ExpectedAnswer, result.Score, analysis.Steps);
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
            if (string.IsNullOrWhiteSpace(answer.AnswerValue))     continue; // no answer given
            if (answer.IsCorrect == true)                          continue; // already matched the key

            // Choice questions compare by letters — no AI needed.
            if (IsChoiceQuestion(q)) continue;

            items.Add(new AnswerGradingService.ItemToGrade(
                i, q.Title, answer.AnswerValue,
                string.IsNullOrWhiteSpace(q.AnswerKey) ? null : q.AnswerKey));
        }

        if (items.Count == 0) return;

        try
        {
            var graded = await _grading.GradeAsync(items);

            foreach (var item in items)
            {
                if (!graded.TryGetValue(item.Index, out var verdict)) continue;

                var answer = result.StudentAnswers.First(a => a.QuestionIndex == item.Index);
                answer.IsCorrect     = verdict.Correct;
                answer.PointsAwarded = verdict.Correct ? 1m : 0m;
                answer.AiSummary     = verdict.Feedback;
                answer.UpdatedAt     = DateTime.UtcNow;
            }
        }
        catch
        {
            // Leave these answers ungraded (shown as N/A); the teacher can grade manually.
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

    /// <summary>Recomputes the aggregate score/status and refreshes the summary text.</summary>
    public void RecomputeResultScore(StudentResult result, int totalQuestions, int passThreshold)
    {
        var correctCount = result.StudentAnswers.Count(a => a.IsCorrect == true);

        result.RecomputeScore(totalQuestions, passThreshold);
        result.AiSummary = $"{correctCount} of {totalQuestions} questions correct.";
    }

    /// <summary>Count of questions a student can actually answer — excludes group headers.</summary>
    public int CountAnswerableQuestions(List<QuestionDto> questions)
        => questions.Count(q => !IsGroupHeader(questions, q));

    /// <summary>
    /// A question referenced by another question's GroupId (e.g. the "Q2" stem
    /// above subpoints "2a"/"2b") is a group header, not a gradeable item — it
    /// renders with no answer input, so it must be excluded from both the
    /// per-question answer loop and the scoring denominator.
    /// </summary>
    public bool IsGroupHeader(List<QuestionDto> questions, QuestionDto q)
        => !string.IsNullOrEmpty(q.Id) && questions.Any(x => x.GroupId == q.Id);

    // ── Internal helpers ─────────────────────────────────────────────────────────

    /// <summary>Inserts or updates the per-question answer for a step submission.</summary>
    private void UpsertQuestionAnswer(
        StudentResult result, QuestionDto question, int questionIndex,
        string imagePath, HandwritingAnalysisDto analysis, bool questionCorrect)
    {
        var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == questionIndex);
        if (answer is null)
        {
            answer = new StudentAnswer
            {
                QuestionIndex   = questionIndex,
                StudentResultId = result.Id
            };
            result.StudentAnswers.Add(answer);
            _db.StudentAnswers.Add(answer);
        }

        answer.QuestionId = question.Id ?? $"q{questionIndex}";
        answer.ImagePath  = imagePath;
        answer.AiSummary  = analysis.Summary;
        answer.UpdatedAt  = DateTime.UtcNow;

        // On a choice question the photo is supporting work, not the answer:
        // the selected option and its grade must stay untouched.
        if (IsChoiceQuestion(question))
        {
            answer.WorkText = string.Join("\n", analysis.Steps.Select(s => s.Text));
            return;
        }

        answer.AnswerValue = analysis.Steps.LastOrDefault()?.Text ?? string.Empty;

        // Don't clobber a teacher's manual grade when a student resubmits.
        if (!answer.TeacherOverridden)
        {
            answer.IsCorrect     = questionCorrect;
            answer.PointsAwarded = questionCorrect ? 1m : 0m;
        }
    }

    /// <summary>
    /// Records the verification outcome on the answer. The grade is flagged for the
    /// teacher when the verifier disagreed with it or was not sure enough.
    /// </summary>
    private static void ApplyVerification(
        StudentResult result, int questionIndex, GradingVerificationDto? verification)
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

        var unsure = !verification.Agrees || verification.Confidence < ConfidenceThreshold;
        answer.NeedsReview = unsure;
        answer.ReviewNote  = unsure
            ? (verification.Agrees
                ? $"Low confidence ({verification.Confidence:0.00}). {verification.Notes}"
                : $"The check disagreed with the first grade ({verification.Confidence:0.00}). {verification.Notes}")
            : null;
    }

    /// <summary>Replaces all step results for a question with the latest evaluation.</summary>
    private void ReplaceStepResults(StudentResult result, int questionIndex, HandwritingAnalysisDto analysis)
    {
        var oldSteps = result.StepResults.Where(s => s.QuestionIndex == questionIndex).ToList();
        _db.StepResults.RemoveRange(oldSteps);
        foreach (var s in oldSteps) result.StepResults.Remove(s);

        foreach (var s in analysis.Steps)
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
    private static bool IsChoiceQuestion(QuestionDto q)
        => q.Type is "1" or "MultipleChoice" or "4" or "SingleChoice";

    /// <summary>Reads a question's options, which are stored as a JSON array.</summary>
    private static List<string> ParseOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return new();
        try { return JsonSerializer.Deserialize<List<string>>(optionsJson) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Resolves an option letter or full option text to its letter (A, B, C…).</summary>
    private static string NormalizeChoiceToken(string token, List<string> options)
    {
        token = token.Trim();
        if (token.Length == 1 && char.IsLetter(token[0]))
            return token.ToUpperInvariant();

        var index = options.FindIndex(o =>
            string.Equals(o?.Trim(), token, StringComparison.OrdinalIgnoreCase));

        return index >= 0 ? ((char)('A' + index)).ToString() : token.ToUpperInvariant();
    }
}
