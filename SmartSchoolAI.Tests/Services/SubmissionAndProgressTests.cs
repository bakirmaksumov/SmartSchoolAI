using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// Handing in work (saved before any AI, one attempt per press of Submit) and the one set of rules the assignment
/// page, the live board and the CSV use — on a real SQLite file, with a separate connection per request.
/// </summary>
public class SubmissionAndProgressTests : IDisposable
{
    private readonly FileDb _db = new();
    private readonly FakeWebHostEnvironment _env = new();

    public void Dispose() { _db.Dispose(); _env.Dispose(); }

    private SubmissionService Submissions(AppDbContext db)
    {
        var config = new ConfigurationBuilder().Build();              // no AI key: every AI call fails
        IHttpClientFactory http = new ThrowingHttpClientFactory();
        var grader = new GradingService(db,
            new AnswerGradingService(config, http, NullLogger<AnswerGradingService>.Instance),
            new HandwritingAnalysisService(config, http, NullLogger<HandwritingAnalysisService>.Instance),
            _env, NullLogger<GradingService>.Instance);
        return new SubmissionService(db, grader, _env, NullLogger<SubmissionService>.Instance);
    }

    private async Task<(User teacher, Class cls, Assignment a, List<User> students)> SetupAsync(int students = 1, int maxAttempts = 1)
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var list = new List<User>();
        for (var i = 0; i < students; i++) list.Add(await _db.UserAsync(Role.Student, $"S{i:00}"));
        var cls = await _db.ClassAsync(t.Id, list.Select(s => s.Id).ToArray());
        var a = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        await using var db = _db.NewContext();
        var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
        row.MaxAttempts = maxAttempts;
        foreach (var s in list) db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s.Id });
        await db.SaveChangesAsync();
        return (t, cls, row, list);
    }

    private static Dictionary<string, string> Answer(string value) => new() { ["q_0"] = value };

    private async Task<SubmissionService.SubmitOutcome> SubmitAsync(Guid student, Guid assignment, Guid key, string answer)
    {
        await using var db = _db.NewContext();
        return await Submissions(db).SubmitAsync(student, assignment, key, Answer(answer));
    }

    private async Task<List<StudentResult>> AttemptsAsync(Guid student, Guid assignment)
    {
        await using var db = _db.NewContext();
        return await db.StudentResults.Include(r => r.StudentAnswers)
            .Where(r => r.StudentId == student && r.AssignmentId == assignment).OrderBy(r => r.AttemptNumber).ToListAsync();
    }

    // ── Handing in ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_IsSavedBeforeAnyAi_AndAFailingAiDoesNotUndoIt()
    {
        var (_, _, a, s) = await SetupAsync();
        var outcome = await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "2 + 2 = four");   // not the key: needs the AI

        Assert.Equal(SubmissionService.SubmitStatus.Saved, outcome.Status);
        Assert.True(outcome.GradingPending);
        var saved = Assert.Single(await AttemptsAsync(s[0].Id, a.Id));
        Assert.True(saved.IsSubmitted);
        Assert.True(saved.GradingPending);

        // The AI is unavailable: marking ends, the hand-in stays.
        await using (var db = _db.NewContext())
            await Submissions(db).GradeAfterSubmitAsync(outcome.ResultId);
        saved = Assert.Single(await AttemptsAsync(s[0].Id, a.Id));
        Assert.True(saved.IsSubmitted);
        Assert.False(saved.GradingPending);
        // The answer the AI could not mark goes to the teacher's review queue instead of staying silently unmarked.
        Assert.True(Assert.Single(saved.StudentAnswers).NeedsReview);
        Assert.True(ReviewRules.Needs(saved));
    }

    [Fact]
    public async Task Submit_WithTheKeysAnswer_NeedsNoAi()
    {
        var (_, _, a, s) = await SetupAsync();
        var outcome = await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "4");
        Assert.False(outcome.GradingPending);
        Assert.Equal(100, Assert.Single(await AttemptsAsync(s[0].Id, a.Id)).Score);
    }

    [Fact]
    public async Task Submit_Retried_WithTheSameKey_ReturnsTheSameAttempt_AndUsesNoOtherAttempt()
    {
        var (_, _, a, s) = await SetupAsync(maxAttempts: 1);
        var key = Guid.NewGuid();

        var first = await SubmitAsync(s[0].Id, a.Id, key, "4");
        var again = await SubmitAsync(s[0].Id, a.Id, key, "4");
        Assert.Equal(SubmissionService.SubmitStatus.Saved, first.Status);
        Assert.Equal(SubmissionService.SubmitStatus.AlreadySaved, again.Status);
        Assert.Equal(first.ResultId, again.ResultId);

        // A genuinely new hand-in (new key) is refused: the only attempt is used.
        var other = await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "4");
        Assert.Equal(SubmissionService.SubmitStatus.NoAttemptsLeft, other.Status);
        Assert.Single(await AttemptsAsync(s[0].Id, a.Id));
    }

    [Fact]
    public async Task Submit_DoubleClick_TwoRequestsAtOnce_MakeOneAttempt_AndNeitherFails()
    {
        var (_, _, a, s) = await SetupAsync(maxAttempts: 0);
        var key = Guid.NewGuid();

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => SubmitAsync(s[0].Id, a.Id, key, "4")));

        Assert.All(outcomes, o => Assert.True(o.IsSaved));
        Assert.Single(outcomes.Select(o => o.ResultId).Distinct());
        Assert.Single(await AttemptsAsync(s[0].Id, a.Id));
    }

    [Fact]
    public async Task Submit_KeepsTheDraftsPhotoAnswer_AndHandsInThatSameAttempt()
    {
        var (_, _, a, s) = await SetupAsync();
        await using (var db = _db.NewContext())
        {
            var draft = new StudentResult { AssignmentId = a.Id, StudentId = s[0].Id, AttemptNumber = 1, IsSubmitted = false };
            draft.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, QuestionId = "q1", AnswerValue = "x = 4", ImagePath = "/uploads/submissions/p.jpg", IsCorrect = true, PointsAwarded = 1 });
            db.StudentResults.Add(draft);
            await db.SaveChangesAsync();
        }

        var outcome = await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "");
        var only = Assert.Single(await AttemptsAsync(s[0].Id, a.Id));
        Assert.Equal(outcome.ResultId, only.Id);
        Assert.True(only.IsSubmitted);
        Assert.Equal("/uploads/submissions/p.jpg", Assert.Single(only.StudentAnswers).ImagePath);
        Assert.Equal(100, only.Score);
    }

    [Fact]
    public async Task Submit_NotGivenOrClosed_IsRefusedWithAReason_AndSavesNothing()
    {
        var (t, cls, a, s) = await SetupAsync();
        var outsider = await _db.UserAsync(Role.Student, "Out");
        var refused = await SubmitAsync(outsider.Id, a.Id, Guid.NewGuid(), "4");
        Assert.Equal(SubmissionService.SubmitStatus.NotAssigned, refused.Status);

        await using (var db = _db.NewContext())
        {
            (await db.Assignments.FirstAsync(x => x.Id == a.Id)).Status = AssignmentStatus.Closed;
            await db.SaveChangesAsync();
        }
        var closed = await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "4");
        Assert.Equal(SubmissionService.SubmitStatus.NotFound, closed.Status);
        Assert.False(string.IsNullOrEmpty(closed.Message));
        Assert.Empty(await AttemptsAsync(s[0].Id, a.Id));
        _ = (t, cls);
    }

    // ── One set of rules ─────────────────────────────────────────────────────

    private async Task<AssignmentProgress> ProgressAsync(Guid assignmentId)
    {
        await using var db = _db.NewContext();
        var a = await db.Assignments
            .Include(x => x.AssignmentStudents).ThenInclude(x => x.Student)
            .Include(x => x.StudentResults).ThenInclude(r => r.Student)
            .Include(x => x.StudentResults).ThenInclude(r => r.StudentAnswers)
            .AsSplitQuery().FirstAsync(x => x.Id == assignmentId);
        return AssignmentProgress.Build(a);
    }

    private async Task AddResultAsync(Guid assignment, Guid student, int attempt, bool submitted, int score, bool photo = false, bool flagged = false)
    {
        await using var db = _db.NewContext();
        var r = new StudentResult { AssignmentId = assignment, StudentId = student, AttemptNumber = attempt, IsSubmitted = submitted, Score = score };
        r.StudentAnswers.Add(new StudentAnswer
        {
            QuestionIndex = 0, QuestionId = "q1", AnswerValue = score > 0 ? "4" : "", IsCorrect = submitted ? score > 0 : null,
            ImagePath = photo ? "/uploads/submissions/x.jpg" : null, NeedsReview = flagged
        });
        db.StudentResults.Add(r);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task TwelveRecipients_TheAcceptanceScenario()
    {
        var (_, _, a, s) = await SetupAsync(students: 12, maxAttempts: 0);
        var (A, B, C) = (s[0].Id, s[1].Id, s[2].Id);
        await AddResultAsync(a.Id, A, 1, submitted: true, score: 0);
        await AddResultAsync(a.Id, A, 2, submitted: true, score: 100);
        await AddResultAsync(a.Id, B, 1, submitted: false, score: 0, photo: true);   // a draft with a photo
        await AddResultAsync(a.Id, C, 1, submitted: true, score: 0);                  // handed in, 0%

        var p = await ProgressAsync(a.Id);
        Assert.Equal(12, p.Assigned);
        Assert.Equal(2, p.SubmittedStudents);
        Assert.Equal(10, p.NotSubmitted);
        Assert.Equal(3, p.SubmittedAttempts);
        Assert.Equal(SubmissionState.InProgress, p.Rows.Single(r => r.Student.Id == B).State);
        var c = p.Rows.Single(r => r.Student.Id == C);
        Assert.Equal(SubmissionState.Submitted, c.State);
        Assert.Equal(0, c.Latest!.Score);
        Assert.Null(p.Rows.Single(r => r.Student.Id == B).Latest);                     // no score for a draft
        Assert.Equal(100, p.Rows.Single(r => r.Student.Id == A).Latest!.Score);       // the newest attempt counts

        // B hands in: 3 submitted, 9 not.
        await using (var db = _db.NewContext())
        {
            var draft = await db.StudentResults.FirstAsync(r => r.StudentId == B);
            draft.IsSubmitted = true;
            await db.SaveChangesAsync();
        }
        p = await ProgressAsync(a.Id);
        Assert.Equal(3, p.SubmittedStudents);
        Assert.Equal(9, p.NotSubmitted);
    }

    [Fact]
    public async Task StartingANewAttemptAfterSubmitting_StaysSubmitted_AndTheDraftIsInTheHistory()
    {
        var (_, _, a, s) = await SetupAsync(maxAttempts: 0);
        await AddResultAsync(a.Id, s[0].Id, 1, submitted: true, score: 100);
        await AddResultAsync(a.Id, s[0].Id, 2, submitted: false, score: 0);

        var row = (await ProgressAsync(a.Id)).Rows.Single();
        Assert.Equal(SubmissionState.Submitted, row.State);
        Assert.Equal(1, row.Latest!.AttemptNumber);
        Assert.Equal(2, row.Draft!.AttemptNumber);
        Assert.Equal(2, row.Attempts.Count);
    }

    [Fact]
    public async Task NeedsReview_CountsStudents_AndFormerRecipientsAreKeptButNotCounted()
    {
        var (_, _, a, s) = await SetupAsync(students: 3, maxAttempts: 0);
        await AddResultAsync(a.Id, s[0].Id, 1, submitted: true, score: 50, flagged: true);
        await AddResultAsync(a.Id, s[0].Id, 2, submitted: true, score: 60, flagged: true);
        await AddResultAsync(a.Id, s[2].Id, 1, submitted: true, score: 90);
        await using (var db = _db.NewContext())
        {
            db.AssignmentStudents.Remove(await db.AssignmentStudents.FirstAsync(x => x.AssignmentId == a.Id && x.StudentId == s[2].Id));
            await db.SaveChangesAsync();
        }

        var p = await ProgressAsync(a.Id);
        Assert.Equal(1, p.NeedsReviewStudents);
        Assert.Equal(2, p.NeedsReviewAttempts);
        Assert.Equal(2, p.Assigned);
        Assert.Equal(1, p.SubmittedStudents);
        var former = Assert.Single(p.Former);
        Assert.Equal(s[2].Id, former.Student.Id);
    }

    [Fact]
    public async Task LiveSignature_IgnoresSavingADraftAgain_ButChangesOnAHandIn()
    {
        var (_, _, a, s) = await SetupAsync(students: 2);
        await AddResultAsync(a.Id, s[0].Id, 1, submitted: false, score: 0);
        var before = (await ProgressAsync(a.Id)).Signature;

        await using (var db = _db.NewContext())
        {
            (await db.StudentResults.FirstAsync(r => r.StudentId == s[0].Id)).UpdatedAt = DateTime.UtcNow;   // the draft is saved again
            await db.SaveChangesAsync();
        }
        Assert.Equal(before, (await ProgressAsync(a.Id)).Signature);

        await SubmitAsync(s[0].Id, a.Id, Guid.NewGuid(), "4");
        Assert.NotEqual(before, (await ProgressAsync(a.Id)).Signature);
    }

    [Fact]
    public async Task QuestionStats_TellNoAnswersFromNoneCorrect_AndCountEachStudentOnce()
    {
        var (_, _, a, s) = await SetupAsync(students: 3, maxAttempts: 0);
        await AddResultAsync(a.Id, s[0].Id, 1, submitted: true, score: 100);   // earlier attempt: right
        await using (var db = _db.NewContext())
        {
            var r = new StudentResult { AssignmentId = a.Id, StudentId = s[0].Id, AttemptNumber = 2, IsSubmitted = true };
            r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, QuestionId = "q1", AnswerValue = "5", IsCorrect = false });
            db.StudentResults.Add(r);
            var r2 = new StudentResult { AssignmentId = a.Id, StudentId = s[1].Id, AttemptNumber = 1, IsSubmitted = true };
            r2.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, QuestionId = "q1", AnswerValue = "3", IsCorrect = false });
            db.StudentResults.Add(r2);
            await db.SaveChangesAsync();
        }
        var questions = new List<QuestionDto> { new() { Id = "q1", Title = "$2+2$" }, new() { Id = "q2", Title = "Unanswered" } };

        var stats = AssignmentsController.BuildQuestionStatsForRecipients(questions, await ProgressAsync(a.Id));
        Assert.Equal(2, stats[0].Answered);       // two students, the newest attempt of each
        Assert.Equal(0, stats[0].Correct);        // "0 of 2 correct"
        Assert.Equal(0, stats[0].SolveRate);
        Assert.Equal(0, stats[1].Answered);       // "No answers"
        Assert.Equal(-1, stats[1].SolveRate);
    }

    [Fact]
    public async Task ClassStatistics_CountStudentsAndAssignments_NotAttempts()
    {
        var (_, cls, a, s) = await SetupAsync(students: 2, maxAttempts: 0);
        await AddResultAsync(a.Id, s[0].Id, 1, submitted: true, score: 40);
        await AddResultAsync(a.Id, s[0].Id, 2, submitted: true, score: 80);
        await AddResultAsync(a.Id, s[1].Id, 1, submitted: false, score: 0);

        await using var db = _db.NewContext();
        var full = await db.Classes.Include(c => c.Assignments).Include(c => c.ClassStudents).FirstAsync(c => c.Id == cls.Id);
        var service = new ClassStatisticsService(db);
        var perAssignment = (await service.ForAssignmentsAsync(full))[a.Id];
        Assert.Equal(2, perAssignment.Assigned);
        Assert.Equal(1, perAssignment.Submitted);          // one student, not two attempts
        Assert.Equal(80, perAssignment.AverageScore);      // the newest attempt
        var perStudent = await service.ForStudentsAsync(full);
        Assert.Equal(1, perStudent[s[0].Id].Submitted);    // one assignment handed in
        Assert.Equal(1, perStudent[s[0].Id].Assigned);
        Assert.Equal(0, perStudent[s[1].Id].Submitted);
    }
}
