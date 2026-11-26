using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>One rule for what a student sees for each piece of work (home page and class page alike).</summary>
public class StudentWorkTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0, DateTimeKind.Utc);

    private static Assignment A(AssignmentType type = AssignmentType.Homework, DateTime? deadline = null, int maxAttempts = 1,
                                bool late = false, DateTime? opens = null)
        => new() { Title = "Quadratics", Type = type, Status = AssignmentStatus.Active, Deadline = deadline,
                   MaxAttempts = maxAttempts, AllowLateSubmission = late, AvailableFrom = opens };

    private static AssignmentStudent E(Assignment a, DateTime? due = null, DateTime? start = null)
        => new() { AssignmentId = a.Id, DueDateOverride = due, StartDateOverride = start };

    private static StudentResult R(int n, bool submitted, int score = 0, bool pending = false, bool review = false)
    {
        var r = new StudentResult { AttemptNumber = n, IsSubmitted = submitted, Score = score, GradingPending = pending, SubmittedAt = Now.AddHours(-n) };
        r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, NeedsReview = review });
        return r;
    }

    [Fact]
    public void A_draft_is_in_progress_never_handed_in()
    {
        var a = A(deadline: Now.AddDays(1));
        var item = StudentWork.Build(a, E(a), new[] { R(1, submitted: false) }, Now);

        Assert.Equal(StudentWorkState.InProgress, item.State);
        Assert.Equal("Continue assignment", item.Primary!.Label);
        Assert.Contains("TakeAssignment", item.Primary.Url);
        Assert.Null(item.LatestSubmitted);
        Assert.Equal("todo", item.Section);
    }

    [Fact]
    public void Nothing_yet_is_start_and_a_later_start_date_is_opens()
    {
        var a = A(deadline: Now.AddDays(3));
        Assert.Equal("Start assignment", StudentWork.Build(a, E(a), Array.Empty<StudentResult>(), Now).Primary!.Label);

        var later = StudentWork.Build(a, E(a, start: Now.AddDays(1)), Array.Empty<StudentResult>(), Now);
        Assert.Equal(StudentWorkState.NotOpenYet, later.State);
        Assert.StartsWith("Opens ", later.StatusText);
        Assert.Null(later.Primary);
    }

    [Fact]
    public void Handed_in_waiting_for_marks_is_submitted_then_feedback_when_available()
    {
        var a = A(deadline: Now.AddDays(1));
        var marking = StudentWork.Build(a, E(a), new[] { R(1, true, pending: true) }, Now);
        Assert.Equal((StudentWorkState.Submitted, "marking", "View submission"), (marking.State, marking.Waiting, marking.Primary!.Label));

        var review = StudentWork.Build(a, E(a), new[] { R(1, true, review: true) }, Now);
        Assert.Equal((StudentWorkState.Submitted, "review"), (review.State, review.Waiting));
        Assert.Null(review.LatestFeedback);                                 // no marks before they are available

        var zero = StudentWork.Build(a, E(a), new[] { R(1, true, score: 0) }, Now);
        Assert.Equal((StudentWorkState.Feedback, "View feedback"), (zero.State, zero.Primary!.Label));
        Assert.Equal(0, zero.LatestFeedback!.Score);                        // a real 0% is a result
    }

    [Fact]
    public void A_second_attempt_keeps_the_first_reachable()
    {
        var a = A(type: AssignmentType.ClassroomPractice, maxAttempts: 0);
        var first = R(1, true, score: 60);
        var item = StudentWork.Build(a, E(a), new[] { first, R(2, false) }, Now);

        Assert.Equal(StudentWorkState.InProgress, item.State);
        Assert.Equal("Continue attempt 2", item.Primary!.Label);
        Assert.Equal("View submitted attempt 1", item.Secondary!.Label);
        Assert.Contains($"resultId={first.Id}", item.Secondary.Url);
    }

    [Fact]
    public void The_students_own_due_date_decides_overdue_and_access()
    {
        var a = A(deadline: Now.AddDays(-1));                               // the class deadline has passed
        var extended = StudentWork.Build(a, E(a, due: Now.AddDays(2)), Array.Empty<StudentResult>(), Now);
        Assert.Equal((StudentWorkState.ToStart, false, true), (extended.State, extended.Overdue, extended.PersonalDue));
        Assert.Equal(Now.AddDays(2), extended.Due);

        var missed = StudentWork.Build(a, E(a), Array.Empty<StudentResult>(), Now);
        Assert.Equal(StudentWorkState.Missed, missed.State);
        Assert.Null(missed.Primary);

        var lateOk = A(deadline: Now.AddDays(-1), late: true);
        var late = StudentWork.Build(lateOk, E(lateOk), Array.Empty<StudentResult>(), Now);
        Assert.Equal((StudentWorkState.ToStart, true), (late.State, late.Overdue));
    }

    [Fact]
    public void Due_dates_read_in_school_time()
    {
        // 13:00 UTC is 18:00 in Tashkent (UTC+5).
        Assert.Equal("Due today, 18:00", StudentWork.DueText(new DateTime(2026, 11, 26, 13, 0, 0, DateTimeKind.Utc), Now));
        Assert.Equal("Due tomorrow, 18:00", StudentWork.DueText(new DateTime(2026, 11, 27, 13, 0, 0, DateTimeKind.Utc), Now));
        Assert.Equal("No due date", StudentWork.DueText(null, Now));
    }
}
