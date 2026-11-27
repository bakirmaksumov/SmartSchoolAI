using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>The notifications bell: real events for each role, only the user's own, and "new" since last opened.</summary>
public class NotificationServiceTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    public void Dispose() => _factory.Dispose();

    private async Task<User> UserAsync(string role, string name)
    {
        var r = await _factory.Context.Roles.FirstAsync(x => x.Name == role);
        var u = new User { Email = $"{name.ToLowerInvariant().Replace(' ', '.')}@school.test", PasswordHash = "x", FullName = name, RoleId = r.Id };
        _factory.Context.Users.Add(u);
        await _factory.Context.SaveChangesAsync();
        return u;
    }

    private async Task<(User Teacher, User Other, User Student, Assignment Mine, Assignment Theirs)> SchoolAsync()
    {
        var teacher = await UserAsync(Role.Teacher, "Teacher One");
        var other = await UserAsync(Role.Teacher, "Teacher Two");
        var student = await UserAsync(Role.Student, "Sam Lee");
        var c1 = new Class { Name = "Pure Maths", Subject = "Math", AcademicYear = "2026", InviteCode = "AAA111", TeacherId = teacher.Id };
        var c2 = new Class { Name = "Other", Subject = "Math", AcademicYear = "2026", InviteCode = "BBB222", TeacherId = other.Id };
        var mine = new Assignment { Title = "Paper 1 practice", TeacherId = teacher.Id, Class = c1, Status = AssignmentStatus.Active };
        var theirs = new Assignment { Title = "Someone else's", TeacherId = other.Id, Class = c2, Status = AssignmentStatus.Active };
        _factory.Context.AddRange(c1, c2, mine, theirs);
        await _factory.Context.SaveChangesAsync();
        return (teacher, other, student, mine, theirs);
    }

    [Fact]
    public async Task A_teacher_sees_work_handed_in_on_their_assignments_and_what_waits_for_review()
    {
        var (teacher, _, student, mine, theirs) = await SchoolAsync();
        var now = DateTime.UtcNow;
        var done = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddHours(-3) };
        var waiting = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddHours(-1), AttemptNumber = 2 };
        waiting.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, NeedsReview = true });
        var draft = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = false, SubmittedAt = now, AttemptNumber = 3 };
        var elsewhere = new StudentResult { AssignmentId = theirs.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now };
        var old = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddDays(-40), AttemptNumber = 4 };
        _factory.Context.StudentResults.AddRange(done, waiting, draft, elsewhere, old);
        _factory.Context.LateJoinRequests.Add(new LateJoinRequest { AssignmentId = mine.Id, StudentId = student.Id, RequestedAt = now.AddMinutes(-30) });
        _factory.Context.QuestionReports.Add(new QuestionReport { AssignmentId = mine.Id, StudentId = student.Id, QuestionIndex = 2, Message = "typo" });
        await _factory.Context.SaveChangesAsync();

        var feed = await new NotificationService(_factory.Context).ForUserAsync(teacher.Id, Role.Teacher);
        Assert.Equal(new[] { "report", "late-join", "review", "submitted" }, feed.Items.Select(i => i.Kind));
        Assert.Equal("Sam Lee's work needs your review", feed.Items[2].Title);
        Assert.Equal($"/Assignments/SubmissionDetail?resultId={waiting.Id}", feed.Items[2].Url);
        Assert.Equal("Paper 1 practice", feed.Items[3].Detail);
        Assert.Equal("Sam Lee reported a problem in question 3", feed.Items[0].Title);
        Assert.Equal(4, feed.Unread);                                                   // never opened the bell
    }

    [Fact]
    public async Task A_student_sees_new_assignments_checked_work_and_late_join_answers_and_only_their_own()
    {
        var (teacher, _, student, mine, theirs) = await SchoolAsync();
        var classmate = await UserAsync(Role.Student, "Ann Kim");
        var now = DateTime.UtcNow;
        var draftAssignment = new Assignment { Title = "Not given yet", TeacherId = teacher.Id, ClassId = mine.ClassId, Status = AssignmentStatus.Draft };
        _factory.Context.Assignments.Add(draftAssignment);
        _factory.Context.AssignmentStudents.AddRange(
            new AssignmentStudent { AssignmentId = mine.Id, StudentId = student.Id, AssignedAt = now.AddDays(-2) },
            new AssignmentStudent { AssignmentId = draftAssignment.Id, StudentId = student.Id, AssignedAt = now },
            new AssignmentStudent { AssignmentId = mine.Id, StudentId = classmate.Id, AssignedAt = now });
        var reviewed = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddDays(-1), TeacherReviewedAt = now.AddHours(-2) };
        var notYet = new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddHours(-1), AttemptNumber = 2 };
        _factory.Context.StudentResults.AddRange(reviewed, notYet);
        _factory.Context.LateJoinRequests.Add(new LateJoinRequest
        {
            AssignmentId = theirs.Id, StudentId = student.Id, Status = LateJoinRequestStatus.Approved, RequestedAt = now.AddDays(-3), DecidedAt = now.AddHours(-5),
        });
        await _factory.Context.SaveChangesAsync();

        var feed = await new NotificationService(_factory.Context).ForUserAsync(student.Id, Role.Student);
        Assert.Equal(new[] { "checked", "join-approved", "assigned" }, feed.Items.Select(i => i.Kind));
        Assert.Equal($"/Student/AssignmentResult?assignmentId={mine.Id}&resultId={reviewed.Id}", feed.Items[0].Url);
        Assert.Equal("Paper 1 practice · Pure Maths", feed.Items[2].Detail);
        Assert.Equal($"/Student/TakeAssignment?assignmentId={theirs.Id}", feed.Items[1].Url);
    }

    [Fact]
    public async Task Opening_the_bell_marks_everything_seen_and_only_later_events_count_as_new()
    {
        var (teacher, _, student, mine, _) = await SchoolAsync();
        var svc = new NotificationService(_factory.Context);
        var now = DateTime.UtcNow;
        _factory.Context.StudentResults.Add(new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddMinutes(-10) });
        await _factory.Context.SaveChangesAsync();
        Assert.Equal(1, (await svc.ForUserAsync(teacher.Id, Role.Teacher)).Unread);

        await svc.MarkSeenAsync(teacher.Id, now);
        Assert.Equal(0, (await svc.ForUserAsync(teacher.Id, Role.Teacher)).Unread);

        _factory.Context.StudentResults.Add(new StudentResult { AssignmentId = mine.Id, StudentId = student.Id, IsSubmitted = true, SubmittedAt = now.AddMinutes(5), AttemptNumber = 2 });
        await _factory.Context.SaveChangesAsync();
        var feed = await svc.ForUserAsync(teacher.Id, Role.Teacher);
        Assert.Equal((2, 1), (feed.Items.Count, feed.Unread));
        Assert.True(feed.Items[0].IsNewSince(feed.SeenAt));
        Assert.False(feed.Items[1].IsNewSince(feed.SeenAt));
    }

    [Fact]
    public async Task An_admin_or_unknown_role_has_an_empty_bell()
    {
        var admin = await UserAsync(Role.Admin, "Admin");
        var feed = await new NotificationService(_factory.Context).ForUserAsync(admin.Id, Role.Admin);
        Assert.Equal((0, 0), (feed.Items.Count, feed.Unread));
    }
}
