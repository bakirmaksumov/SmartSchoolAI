using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// The student's final "Submit", in two clearly separated steps:
///
/// 1. <see cref="SubmitAsync"/> saves the attempt as submitted — answers, drawings, work, the score that can be
///    worked out without AI — in one database transaction, and nothing else. Once it returns
///    <see cref="SubmitStatus.Saved"/> the work is handed in, whatever happens next. It never waits for the AI.
/// 2. <see cref="GradeAfterSubmitAsync"/> lets the AI mark the answers that need it (no key, a different but
///    possibly equivalent form, drawings) and updates the score. A failure or a slow AI there can only leave
///    those answers unmarked for the teacher; it can never undo or delay the submission.
///
/// Every press of Submit carries a key from the page. A repeated request with the same key (a retry after a lost
/// response, a double click) returns the attempt it already saved and never uses up another attempt. With SQLite
/// the transaction is BEGIN IMMEDIATE, so two such requests run one after the other, and a unique index on
/// (student, key) makes a second attempt impossible even so.
/// </summary>
public class SubmissionService
{
    private readonly AppDbContext _db;
    private readonly GradingService _grader;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<SubmissionService> _logger;

    public SubmissionService(AppDbContext db, GradingService grader, IWebHostEnvironment env, ILogger<SubmissionService> logger)
    {
        _db = db;
        _grader = grader;
        _env = env;
        _logger = logger;
    }

    public enum SubmitStatus { Saved, AlreadySaved, NotFound, NotAssigned, NoAttemptsLeft }

    public sealed record SubmitOutcome(SubmitStatus Status, Guid AssignmentId, Guid ClassId, Guid ResultId, bool GradingPending, string? Message)
    {
        public bool IsSaved => Status is SubmitStatus.Saved or SubmitStatus.AlreadySaved;
    }

    /// <summary>Largest drawing accepted from the sketch pad (decoded bytes).</summary>
    public const int MaxSketchBytes = 3 * 1024 * 1024;

    public async Task<SubmitOutcome> SubmitAsync(Guid studentId, Guid assignmentId, Guid submissionKey, IReadOnlyDictionary<string, string> form)
    {
        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync();

        // The same press of Submit again: the attempt it saved, never a new one.
        var already = await _db.StudentResults
            .Where(r => r.StudentId == studentId && r.SubmissionKey == submissionKey && r.IsSubmitted)
            .Select(r => new { r.Id, r.AssignmentId, r.GradingPending, r.Assignment.ClassId })
            .FirstOrDefaultAsync();
        if (already is not null)
            return new(SubmitStatus.AlreadySaved, already.AssignmentId, already.ClassId, already.Id, already.GradingPending, null);

        var assignment = await _db.Assignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId && a.Status == AssignmentStatus.Active);
        if (assignment is null)
            return new(SubmitStatus.NotFound, assignmentId, Guid.Empty, Guid.Empty, false,
                       "This assignment is not open for submissions any more. Ask your teacher.");

        if (!await _db.AssignmentStudents.AnyAsync(x => x.AssignmentId == assignmentId && x.StudentId == studentId))
            return new(SubmitStatus.NotAssigned, assignmentId, assignment.ClassId, Guid.Empty, false,
                       "This assignment is not given to you.");

        var submittedCount = await _db.StudentResults
            .CountAsync(r => r.AssignmentId == assignmentId && r.StudentId == studentId && r.IsSubmitted);
        if (!assignment.AllowsUnlimitedAttempts && submittedCount >= assignment.MaxAttempts)
            return new(SubmitStatus.NoAttemptsLeft, assignmentId, assignment.ClassId, Guid.Empty, false,
                       "You have no attempts left for this assignment.");

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(assignment.Questions) ?? new();

        // Answers go into the attempt in progress, so per-question photos already saved there are kept.
        var result = await _grader.GetOrCreateResultAsync(assignmentId, studentId);

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (_grader.IsGroupHeader(questions, q)) continue;   // a shared stem has nothing to answer

            var existing = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == i);
            if (existing != null && !string.IsNullOrEmpty(existing.ImagePath))
                continue;                                            // a photographed solution, already marked

            var answerValue = form.TryGetValue($"q_{i}", out var v) ? (v ?? string.Empty) : string.Empty;
            var isCorrect   = _grader.EvaluateTypedAnswer(q, answerValue);

            if (existing is null)
            {
                existing = new StudentAnswer { StudentResultId = result.Id, QuestionIndex = i };
                result.StudentAnswers.Add(existing);
                _db.StudentAnswers.Add(existing);
            }

            if (!existing.TeacherOverridden)
            {
                existing.QuestionId    = q.Id ?? $"q{i}";
                existing.AnswerValue   = answerValue;
                existing.IsCorrect     = isCorrect;
                existing.PointsAwarded = isCorrect == true ? q.MaxPoints : 0m;
            }

            var work = form.TryGetValue($"work_{i}", out var w) ? w?.Trim() : null;
            if (!string.IsNullOrWhiteSpace(work)) existing.WorkText = work;
            existing.UpdatedAt = now;
        }

        await SaveSketchesAsync(form, questions, result, studentId, now);

        result.IsSubmitted    = true;
        result.SubmittedAt    = now;
        result.SubmissionKey  = submissionKey;
        result.GradingPending = NeedsAi(result, questions);
        _grader.RecomputeResultScore(result, questions, assignment.PassThreshold);

        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        return new(SubmitStatus.Saved, assignmentId, assignment.ClassId, result.Id, result.GradingPending, null);
    }

    /// <summary>
    /// The AI's part, after the submission is saved: marks typed answers the key could not decide and drawings,
    /// then updates the score. Always clears <see cref="StudentResult.GradingPending"/>, even when the AI fails —
    /// those answers then stay unmarked (or flagged) for the teacher.
    /// </summary>
    public async Task GradeAfterSubmitAsync(Guid resultId)
    {
        var result = await _db.StudentResults
            .Include(r => r.StudentAnswers)
            .Include(r => r.Assignment)
            .FirstOrDefaultAsync(r => r.Id == resultId && r.IsSubmitted);
        if (result is null) return;

        var questions = JsonSerializer.Deserialize<List<QuestionDto>>(result.Assignment.Questions) ?? new();
        try
        {
            await _grader.GradeUngradedAnswersAsync(result, questions);
            await _grader.GradeSketchesAsync(await SketchesToGradeAsync(result, questions));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI marking after submission failed for result {ResultId}", resultId);
            ActivityLog.Record(_env, "SubmitGradingFailed", "Student", result.StudentId, $"assignment={result.AssignmentId} result={resultId}");
        }
        finally
        {
            result.GradingPending = false;
            _grader.RecomputeResultScore(result, questions, result.Assignment.PassThreshold);
            result.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>True when an answer of this attempt still needs the AI: a drawing, or a typed answer the key did not settle.</summary>
    public bool NeedsAi(StudentResult result, List<QuestionDto> questions)
    {
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (_grader.IsGroupHeader(questions, q)) continue;
            var a = result.StudentAnswers.FirstOrDefault(x => x.QuestionIndex == i);
            if (a is null || a.TeacherOverridden || !string.IsNullOrEmpty(a.ImagePath)) continue;
            if (!string.IsNullOrEmpty(a.SketchPath)) return true;
            if (!string.IsNullOrWhiteSpace(a.AnswerValue) && a.IsCorrect != true && !GradingService.IsChoiceQuestion(q)) return true;
        }
        return false;
    }

    private async Task<List<GradingService.SketchToGrade>> SketchesToGradeAsync(StudentResult result, List<QuestionDto> questions)
    {
        var list = new List<GradingService.SketchToGrade>();
        foreach (var a in result.StudentAnswers.Where(a => !string.IsNullOrEmpty(a.SketchPath) && !a.TeacherOverridden && string.IsNullOrEmpty(a.ImagePath)))
        {
            if (a.QuestionIndex < 0 || a.QuestionIndex >= questions.Count) continue;
            var path = SubmissionStorage.FindFile(_env, result.StudentId, Path.GetFileName(a.SketchPath!));
            if (path is null) continue;
            list.Add(new GradingService.SketchToGrade(a, questions[a.QuestionIndex], QuestionText(questions, questions[a.QuestionIndex]),
                                                      await File.ReadAllBytesAsync(path)));
        }
        return list;
    }

    private static string QuestionText(List<QuestionDto> questions, QuestionDto q)
    {
        var parent = string.IsNullOrEmpty(q.GroupId) ? null : questions.FirstOrDefault(x => x.Id == q.GroupId);
        return parent is null || string.IsNullOrWhiteSpace(parent.Title) ? q.Title : $"{parent.Title}\n{q.Title}";
    }

    /// <summary>
    /// Stores the picture from each drawing question's sketch pad (a PNG data URL in "sketchpng_{i}") on the
    /// student's answer. Only questions the teacher marked as drawing questions are read, and only real PNG data.
    /// </summary>
    private async Task SaveSketchesAsync(IReadOnlyDictionary<string, string> form, List<QuestionDto> questions,
                                         StudentResult result, Guid studentId, DateTime now)
    {
        const string prefix = "data:image/png;base64,";

        for (int i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            if (!q.RequireDrawing || _grader.IsGroupHeader(questions, q)) continue;
            if (!form.TryGetValue($"sketchpng_{i}", out var dataUrl) ||
                string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            byte[] png;
            try { png = Convert.FromBase64String(dataUrl[prefix.Length..]); }
            catch (FormatException) { continue; }
            if (png.Length == 0 || png.Length > MaxSketchBytes || !FileSignature.IsSupportedImage(png) || png[0] != 0x89)
                continue;

            var answer = result.StudentAnswers.FirstOrDefault(a => a.QuestionIndex == i);
            if (answer is null)
            {
                answer = new StudentAnswer { StudentResultId = result.Id, QuestionIndex = i, QuestionId = q.Id ?? $"q{i}" };
                result.StudentAnswers.Add(answer);
                _db.StudentAnswers.Add(answer);
            }

            answer.SketchPath = await SubmissionStorage.SaveAsync(_env, png, ".png", studentId);
            answer.UpdatedAt  = now;

            // A drawing is marked from the picture (after submitting), not from the typed text.
            if (!answer.TeacherOverridden && string.IsNullOrEmpty(answer.ImagePath))
            {
                answer.IsCorrect     = null;
                answer.PointsAwarded = null;
            }
        }
    }
}
