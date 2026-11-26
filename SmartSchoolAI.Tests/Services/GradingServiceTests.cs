using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// Covers the grading logic extracted into <see cref="GradingService"/>: answer-key
/// checking, group-header handling, deadline/availability windows, score aggregation,
/// result creation, and assignment access.
///
/// The two AI-driven methods (AiCheckTypedAnswerAsync, EvaluateStepsAsync) call
/// OpenRouter and are exercised end-to-end against the running app, not here. These
/// tests build the service with an empty configuration and an HTTP factory that
/// throws, so any accidental network call fails loudly instead of costing money.
/// </summary>
public class GradingServiceTests : IDisposable
{
    private readonly TestDbContextFactory   _factory = new();
    private readonly FakeWebHostEnvironment _env     = new();
    private readonly GradingService         _sut;

    public GradingServiceTests()
    {
        var config = new ConfigurationBuilder().Build();   // no OpenRouter:ApiKey
        IHttpClientFactory http = new ThrowingHttpClientFactory();

        _sut = AiTestServices.Grading(_factory.Context, _factory.NewContext, config, http, _env);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _env.Dispose();
    }

    // ── EvaluateTypedAnswer ──────────────────────────────────────────────────────

    [Fact]
    public void EvaluateTypedAnswer_NoAnswerKey_ReturnsNull()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = null };
        Assert.Null(_sut.EvaluateTypedAnswer(q, "anything"));
    }

    [Fact]
    public void EvaluateTypedAnswer_EmptyStudentAnswer_ReturnsNull()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "5" };
        Assert.Null(_sut.EvaluateTypedAnswer(q, ""));
    }

    [Fact]
    public void EvaluateTypedAnswer_TextExactMatch_ReturnsTrue()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "42" };
        Assert.True(_sut.EvaluateTypedAnswer(q, "42"));
    }

    [Fact]
    public void EvaluateTypedAnswer_TextMatchIsCaseInsensitiveAndTrimmed_ReturnsTrue()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "Paris" };
        Assert.True(_sut.EvaluateTypedAnswer(q, "  paris "));
    }

    [Fact]
    public void EvaluateTypedAnswer_TextMismatch_ReturnsFalse()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "42" };
        Assert.False(_sut.EvaluateTypedAnswer(q, "43"));
    }

    // A math-field answer box (used for every text/numeric question) submits bare
    // LaTeX wrapped in "$…$", and MathLive backslash-escapes characters LaTeX
    // treats specially — typing "25%" is stored as the literal string "$25\%$".
    // A plain-text teacher answer key must still match it exactly.
    [Fact]
    public void EvaluateTypedAnswer_MathFieldPercent_MatchesPlainTextKey()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "25%" };
        Assert.True(_sut.EvaluateTypedAnswer(q, "$25\\%$"));
    }

    [Fact]
    public void EvaluateTypedAnswer_MathFieldPlainNumber_StillMatches()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "210" };
        Assert.True(_sut.EvaluateTypedAnswer(q, "$210$"));
    }

    [Fact]
    public void EvaluateTypedAnswer_MathFieldWrongAnswer_StillReturnsFalse()
    {
        var q = new QuestionDto { Title = "Q", AnswerKey = "25%" };
        Assert.False(_sut.EvaluateTypedAnswer(q, "$30\\%$"));
    }

    [Fact]
    public void EvaluateTypedAnswer_ChoiceMatchingLetter_ReturnsTrue()
    {
        var q = ChoiceQuestion(answerKey: "C", options: new[] { "A opt", "B opt", "C opt", "D opt" });
        Assert.True(_sut.EvaluateTypedAnswer(q, "C"));
    }

    [Fact]
    public void EvaluateTypedAnswer_ChoiceWrongLetter_ReturnsFalse()
    {
        var q = ChoiceQuestion(answerKey: "C", options: new[] { "A opt", "B opt", "C opt", "D opt" });
        Assert.False(_sut.EvaluateTypedAnswer(q, "A"));
    }

    [Fact]
    public void EvaluateTypedAnswer_MultiChoiceIsOrderIndependent_ReturnsTrue()
    {
        var q = ChoiceQuestion(answerKey: "A,C", options: new[] { "A opt", "B opt", "C opt" });
        Assert.True(_sut.EvaluateTypedAnswer(q, "C,A"));
    }

    [Fact]
    public void EvaluateTypedAnswer_ChoiceKeyAsOptionText_NormalizesToLetter()
    {
        // Teacher typed the option's full text as the key; student submits the letter.
        var q = ChoiceQuestion(answerKey: "Banana", options: new[] { "Apple", "Banana", "Cherry" });
        Assert.True(_sut.EvaluateTypedAnswer(q, "B"));
    }

    // ── IsGroupHeader / CountAnswerableQuestions ─────────────────────────────────

    [Fact]
    public void IsGroupHeader_QuestionWithChildren_ReturnsTrue()
    {
        var questions = new List<QuestionDto>
        {
            new() { Id = "q2", Title = "Q2 stem" },
            new() { Id = "q2a", Title = "2a", GroupId = "q2" },
        };
        Assert.True(_sut.IsGroupHeader(questions, questions[0]));
    }

    [Fact]
    public void IsGroupHeader_StandaloneQuestion_ReturnsFalse()
    {
        var questions = new List<QuestionDto> { new() { Id = "q1", Title = "Q1" } };
        Assert.False(_sut.IsGroupHeader(questions, questions[0]));
    }

    [Fact]
    public void CountAnswerableQuestions_ExcludesGroupHeaders()
    {
        var questions = new List<QuestionDto>
        {
            new() { Id = "q2",  Title = "Q2 stem" },          // header — not answerable
            new() { Id = "q2a", Title = "2a", GroupId = "q2" },
            new() { Id = "q2b", Title = "2b", GroupId = "q2" },
            new() { Id = "q3",  Title = "Q3" },
        };
        Assert.Equal(3, _sut.CountAnswerableQuestions(questions));
    }

    // ── IsPastDeadline / IsBeforeStart (static) ──────────────────────────────────

    [Fact]
    public void IsPastDeadline_HomeworkPastDeadlineNoLate_ReturnsTrue()
    {
        var a = new Assignment { Type = AssignmentType.Homework, Deadline = DateTime.UtcNow.AddDays(-1), AllowLateSubmission = false };
        Assert.True(GradingService.IsPastDeadline(a, new AssignmentStudent()));
    }

    [Fact]
    public void IsPastDeadline_HomeworkLateAllowed_ReturnsFalse()
    {
        var a = new Assignment { Type = AssignmentType.Homework, Deadline = DateTime.UtcNow.AddDays(-1), AllowLateSubmission = true };
        Assert.False(GradingService.IsPastDeadline(a, new AssignmentStudent()));
    }

    [Fact]
    public void IsPastDeadline_ClassroomPractice_IsNeverGated()
    {
        var a = new Assignment { Type = AssignmentType.ClassroomPractice, Deadline = DateTime.UtcNow.AddDays(-1) };
        Assert.False(GradingService.IsPastDeadline(a, new AssignmentStudent()));
    }

    [Fact]
    public void IsPastDeadline_StudentDueDateOverrideWins()
    {
        var a = new Assignment { Type = AssignmentType.Homework, Deadline = DateTime.UtcNow.AddDays(-5) };
        var enrollment = new AssignmentStudent { DueDateOverride = DateTime.UtcNow.AddDays(3) };
        Assert.False(GradingService.IsPastDeadline(a, enrollment));   // their personal date hasn't passed
    }

    [Fact]
    public void IsBeforeStart_AvailableFromInFuture_ReturnsTrue()
    {
        var a = new Assignment { AvailableFrom = DateTime.UtcNow.AddDays(1) };
        Assert.True(GradingService.IsBeforeStart(a, new AssignmentStudent()));
    }

    [Fact]
    public void IsBeforeStart_NoStartDate_ReturnsFalse()
    {
        Assert.False(GradingService.IsBeforeStart(new Assignment(), new AssignmentStudent()));
    }

    [Fact]
    public void IsBeforeStart_StudentStartOverrideWins()
    {
        var a = new Assignment { AvailableFrom = DateTime.UtcNow.AddDays(-5) };
        var enrollment = new AssignmentStudent { StartDateOverride = DateTime.UtcNow.AddDays(2) };
        Assert.True(GradingService.IsBeforeStart(a, enrollment));   // their window hasn't opened yet
    }

    // ── RecomputeResultScore ─────────────────────────────────────────────────────

    [Fact]
    public void RecomputeResultScore_PartialScore_SetsSummaryAndPartialStatus()
    {
        var result = new StudentResult
        {
            StudentAnswers =
            {
                new StudentAnswer { QuestionIndex = 0, IsCorrect = true,  PointsAwarded = 1m },
                new StudentAnswer { QuestionIndex = 1, IsCorrect = true,  PointsAwarded = 1m },
                new StudentAnswer { QuestionIndex = 2, IsCorrect = false, PointsAwarded = 0m },
            }
        };

        _sut.RecomputeResultScore(result, totalQuestions: 3, passThreshold: 70);

        Assert.Equal(67, result.Score);                       // 2/3 → 67%
        Assert.Equal(ResultStatus.Partial, result.OverallStatus);
        Assert.Contains("2 of 3", result.AiSummary);
    }

    [Fact]
    public void RecomputeResultScore_AllCorrect_IsCorrectStatus()
    {
        var result = new StudentResult
        {
            StudentAnswers =
            {
                new StudentAnswer { QuestionIndex = 0, IsCorrect = true, PointsAwarded = 1m },
                new StudentAnswer { QuestionIndex = 1, IsCorrect = true, PointsAwarded = 1m },
            }
        };

        _sut.RecomputeResultScore(result, totalQuestions: 2, passThreshold: 70);

        Assert.Equal(100, result.Score);
        Assert.Equal(ResultStatus.Correct, result.OverallStatus);
    }

    [Fact]
    public void RecomputeResultScore_NoneCorrect_IsIncorrectStatus()
    {
        var result = new StudentResult
        {
            StudentAnswers = { new StudentAnswer { QuestionIndex = 0, IsCorrect = false, PointsAwarded = 0m } }
        };

        _sut.RecomputeResultScore(result, totalQuestions: 2, passThreshold: 70);

        Assert.Equal(0, result.Score);
        Assert.Equal(ResultStatus.Incorrect, result.OverallStatus);
    }

    // ── Marks per question ───────────────────────────────────────────────────────

    [Fact]
    public void QuestionDto_MaxPoints_DefaultsToOneAndStaysInRange()
    {
        Assert.Equal(1m, new QuestionDto().MaxPoints);                 // saved before marks existed
        Assert.Equal(1m, new QuestionDto { Marks = 0 }.MaxPoints);
        Assert.Equal(4m, new QuestionDto { Marks = 4 }.MaxPoints);
        Assert.Equal(100m, new QuestionDto { Marks = 5000 }.MaxPoints);
    }

    [Fact]
    public void QuestionDto_ReadsMarksAndDrawingFlagFromJson()
    {
        var q = JsonSerializer.Deserialize<QuestionDto>("""{ "title": "x", "marks": "3", "requireDrawing": true }""")!;
        Assert.Equal(3, q.Marks);
        Assert.True(q.RequireDrawing);
    }

    [Fact]
    public void TotalMarks_SumsAnswerableQuestionsAndSkipsStems()
    {
        var questions = new List<QuestionDto>
        {
            new() { Id = "q2", Title = "Shared stem", Marks = 9 },       // has parts → carries no marks
            new() { Id = "a", GroupId = "q2", Marks = 1 },
            new() { Id = "b", GroupId = "q2", Marks = 4 },
            new() { Id = "q3", Marks = null },                           // counts as 1
        };

        Assert.Equal(6m, GradingService.TotalMarks(questions));
    }

    [Fact]
    public void RecomputeResultScore_WithMarks_WeighsQuestionsByMarks()
    {
        var questions = new List<QuestionDto>
        {
            new() { Id = "1", Marks = 4 },
            new() { Id = "2", Marks = 1 },
        };
        var result = new StudentResult
        {
            StudentAnswers =
            {
                new StudentAnswer { QuestionIndex = 0, IsCorrect = true,  PointsAwarded = 4m },
                new StudentAnswer { QuestionIndex = 1, IsCorrect = false, PointsAwarded = 0m },
            }
        };

        _sut.RecomputeResultScore(result, questions, passThreshold: 70);

        Assert.Equal(80, result.Score);                       // 4 of 5 marks, not 1 of 2 questions
        Assert.Equal(ResultStatus.Correct, result.OverallStatus);
        Assert.Contains("4 of 5 marks", result.AiSummary);
    }

    [Theory]
    [InlineData(true,  null, 4, 4)]     // correct → full marks
    [InlineData(false, null, 4, 0)]     // wrong with no partial mark → nothing
    [InlineData(false, 2.0,  4, 2)]     // partial credit kept
    [InlineData(false, 9.0,  4, 4)]     // never more than the question is worth
    [InlineData(false, -1.0, 4, 0)]     // never negative
    public void PointsFor_UsesPartialMarksWithinRange(bool correct, double? marks, int max, int expected)
        => Assert.Equal((decimal)expected, GradingService.PointsFor(correct, (decimal?)marks, max));

    [Fact]
    public async Task GradeSketchesAsync_WhenAiUnavailable_LeavesDrawingUnmarkedForTeacher()
    {
        var answer = new StudentAnswer { QuestionIndex = 0, IsCorrect = false, PointsAwarded = 0m, SketchPath = "/x.png" };
        var question = new QuestionDto { Title = "Sketch y = f⁻¹(x)", Marks = 1, RequireDrawing = true };

        await _sut.GradeSketchesAsync(new[] { new GradingService.SketchToGrade(answer, question, question.Title, new byte[] { 0x89 }) });

        Assert.Null(answer.IsCorrect);
        Assert.Null(answer.PointsAwarded);
        Assert.True(answer.NeedsReview);
        Assert.Contains("mark it yourself", answer.ReviewNote);
    }

    // ── GetOrCreateResultAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetOrCreateResultAsync_NoExistingResult_CreatesFirstAttempt()
    {
        var assignmentId = Guid.NewGuid();
        var studentId    = Guid.NewGuid();

        var result = await _sut.GetOrCreateResultAsync(assignmentId, studentId);

        Assert.Equal(1, result.AttemptNumber);
        Assert.False(result.IsSubmitted);
        Assert.Equal(assignmentId, result.AssignmentId);
    }

    [Fact]
    public async Task GetOrCreateResultAsync_InProgressResultExists_ReturnsSameResult()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true);
        var existing = new StudentResult { AssignmentId = assignmentId, StudentId = studentId, IsSubmitted = false, AttemptNumber = 1 };
        _factory.Context.StudentResults.Add(existing);
        await _factory.Context.SaveChangesAsync();

        var result = await _sut.GetOrCreateResultAsync(assignmentId, studentId);

        Assert.Equal(existing.Id, result.Id);
    }

    [Fact]
    public async Task GetOrCreateResultAsync_OnlySubmittedResultsExist_CreatesNextAttempt()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true);
        _factory.Context.StudentResults.Add(new StudentResult { AssignmentId = assignmentId, StudentId = studentId, IsSubmitted = true, AttemptNumber = 1 });
        await _factory.Context.SaveChangesAsync();

        var result = await _sut.GetOrCreateResultAsync(assignmentId, studentId);

        Assert.NotEqual(default, result.Id);
        Assert.False(result.IsSubmitted);
        Assert.Equal(2, result.AttemptNumber);
    }

    // ── LoadAssignedQuestionAsync ────────────────────────────────────────────────

    [Fact]
    public async Task LoadAssignedQuestionAsync_AssignmentNotActive_ReturnsError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Draft, enroll: true);

        var (_, question, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 0, studentId);

        Assert.Null(question);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task LoadAssignedQuestionAsync_StudentNotEnrolled_ReturnsError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: false);

        var (_, question, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 0, studentId);

        Assert.Null(question);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task LoadAssignedQuestionAsync_BeforeStartWindow_ReturnsError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true,
                                          availableFrom: DateTime.UtcNow.AddDays(1));

        var (_, _, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 0, studentId);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task LoadAssignedQuestionAsync_PastDeadlineHomework_ReturnsError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true,
                                          type: AssignmentType.Homework, deadline: DateTime.UtcNow.AddDays(-1));

        var (_, _, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 0, studentId);

        Assert.NotNull(error);
    }

    [Fact]
    public async Task LoadAssignedQuestionAsync_IndexOutOfRange_ReturnsError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true);

        var (_, question, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 99, studentId);

        Assert.Null(question);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task LoadAssignedQuestionAsync_ValidAccess_ReturnsQuestionAndNoError()
    {
        var (assignmentId, studentId) = await SeedAssignmentAsync(AssignmentStatus.Active, enroll: true);

        var (assignment, question, error) = await _sut.LoadAssignedQuestionAsync(assignmentId, 0, studentId);

        Assert.Null(error);
        Assert.NotNull(assignment);
        Assert.NotNull(question);
        Assert.Equal("First question", question!.Title);
    }

    // ── GradeUngradedAnswersAsync (no-AI branches) ───────────────────────────────

    [Fact]
    public async Task GradeUngradedAnswersAsync_OnlyChoiceAnswers_MakesNoAiCallAndDoesNotThrow()
    {
        var questions = new List<QuestionDto>
        {
            ChoiceQuestion(answerKey: "A", options: new[] { "A opt", "B opt" }),
        };
        var result = new StudentResult
        {
            StudentAnswers = { new StudentAnswer { QuestionIndex = 0, AnswerValue = "A", IsCorrect = true } }
        };

        // No gradable items → returns before ever touching the AI (which would throw here).
        await _sut.GradeUngradedAnswersAsync(result, questions);

        Assert.True(result.StudentAnswers.First().IsCorrect == true);   // untouched
    }

    [Fact]
    public async Task GradeUngradedAnswersAsync_AlreadyCorrectAnswer_IsSkipped()
    {
        var questions = new List<QuestionDto> { new() { Id = "q1", Title = "Free text", AnswerKey = null } };
        var result = new StudentResult
        {
            StudentAnswers = { new StudentAnswer { QuestionIndex = 0, AnswerValue = "already right", IsCorrect = true } }
        };

        await _sut.GradeUngradedAnswersAsync(result, questions);

        Assert.True(result.StudentAnswers.First().IsCorrect == true);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static QuestionDto ChoiceQuestion(string answerKey, string[] options) => new()
    {
        Id        = Guid.NewGuid().ToString(),
        Title     = "Choice question",
        Type      = "MultipleChoice",
        AnswerKey = answerKey,
        Options   = JsonSerializer.Serialize(options)
    };

    /// <summary>
    /// Seeds the full graph the foreign keys require — a teacher, a class, the
    /// assignment, and a student — optionally enrolling the student. Returns the
    /// assignment and student ids the access tests then exercise.
    /// </summary>
    private async Task<(Guid assignmentId, Guid studentId)> SeedAssignmentAsync(
        AssignmentStatus status, bool enroll,
        AssignmentType type = AssignmentType.ClassroomPractice,
        DateTime? deadline = null, DateTime? availableFrom = null)
    {
        var ctx = _factory.Context;
        var teacherRole = await ctx.Roles.FirstAsync(r => r.Name == Role.Teacher);
        var studentRole = await ctx.Roles.FirstAsync(r => r.Name == Role.Student);

        var teacher = new User { Email = $"t{Guid.NewGuid():N}@test", PasswordHash = "x", FullName = "Teacher", RoleId = teacherRole.Id };
        var student = new User { Email = $"s{Guid.NewGuid():N}@test", PasswordHash = "x", FullName = "Student", RoleId = studentRole.Id };
        ctx.Users.AddRange(teacher, student);

        var cls = new Class
        {
            Name = "Class", Subject = "Math", AcademicYear = "2026",
            InviteCode = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            TeacherId = teacher.Id
        };
        ctx.Classes.Add(cls);

        var assignment = new Assignment
        {
            Id            = Guid.NewGuid(),
            Title         = "Test assignment",
            Status        = status,
            Type          = type,
            Deadline      = deadline,
            AvailableFrom = availableFrom,
            ClassId       = cls.Id,
            TeacherId     = teacher.Id,
            Questions     = """[{"id":"q1","title":"First question","type":"text","answerKey":"5"}]"""
        };
        ctx.Assignments.Add(assignment);

        if (enroll)
            ctx.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = assignment.Id, StudentId = student.Id });

        await ctx.SaveChangesAsync();
        return (assignment.Id, student.Id);
    }

}
