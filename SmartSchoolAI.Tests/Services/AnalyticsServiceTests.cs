using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using SmartSchoolAI.ViewModels;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// The analytics page counts work (one assignment given to one student), and every number agrees with the list it
/// opens — on a real SQLite file built with the app's migrations.
/// </summary>
public class AnalyticsServiceTests : IDisposable
{
    private readonly FileDb _db = new();
    public void Dispose() => _db.Dispose();

    private static readonly DateTime Now = new(2026, 11, 25, 9, 0, 0, DateTimeKind.Utc);

    // Two questions: q1 worth 1 mark, q2 worth 3 marks.
    private const string TwoQuestions =
        "[{\"id\":\"q1\",\"title\":\"$2+2$\",\"type\":\"0\",\"answerKey\":\"4\"},{\"id\":\"q2\",\"title\":\"Explain\",\"type\":\"2\",\"marks\":3}]";

    private async Task<(User teacher, Class cls, List<User> students)> ClassAsync(int students)
    {
        var t = await _db.UserAsync(Role.Teacher, "Teacher");
        var list = new List<User>();
        for (var i = 0; i < students; i++) list.Add(await _db.UserAsync(Role.Student, $"Pupil {i:00}"));
        var cls = await _db.ClassAsync(t.Id, list.Select(s => s.Id).ToArray());
        return (t, cls, list);
    }

    private async Task<Assignment> GiveAsync(Class cls, Guid teacher, IEnumerable<User> to, AssignmentType type = AssignmentType.Homework,
        DateTime? deadline = null, DateTime? assignedAt = null)
    {
        var a = await _db.AssignmentAsync(cls.Id, teacher, type, AssignmentStatus.Active, deadline);
        await using var db = _db.NewContext();
        var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
        row.Questions = TwoQuestions;
        foreach (var s in to)
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s.Id, AssignedAt = assignedAt ?? Now.AddDays(-1) });
        await db.SaveChangesAsync();
        return row;
    }

    /// <summary>An attempt with the given marks for q1 and q2 (null = not marked); flag = waiting for the teacher.</summary>
    private async Task<StudentResult> AttemptAsync(Guid assignment, Guid student, int attempt, bool submitted, int score,
        decimal? q1 = 1, decimal? q2 = 3, bool flag = false, bool aiPending = false)
    {
        await using var db = _db.NewContext();
        var r = new StudentResult
        {
            AssignmentId = assignment, StudentId = student, AttemptNumber = attempt, IsSubmitted = submitted,
            Score = score, GradingPending = aiPending, SubmittedAt = Now.AddHours(-attempt)
        };
        r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, QuestionId = "q1", AnswerValue = "4", PointsAwarded = q1, IsCorrect = q1 is null ? null : q1 > 0 });
        r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 1, QuestionId = "q2", AnswerValue = "because", PointsAwarded = flag ? null : q2,
                                                 IsCorrect = flag || q2 is null ? null : q2 == 3, NeedsReview = flag });
        db.StudentResults.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<AnalyticsViewModel?> BuildAsync(Guid teacher, Guid? cls, string? tab = null, string? type = null,
        string? period = null, string? status = null, Guid? assignment = null, Guid? questionAssignment = null)
    {
        await using var db = _db.NewContext();
        return await new AnalyticsService(db).BuildAsync(teacher,
            new AnalyticsService.Request(cls, tab, type, period, status, assignment, null, questionAssignment), Now);
    }

    private static void AssertAddsUp(AnalyticsViewModel m)
    {
        Assert.Equal(m.Assigned, m.NotSubmitted + m.AwaitingReview + m.Graded);
        Assert.Equal(m.Submitted, m.AwaitingReview + m.Graded);
        // The list each counter opens has exactly that many items.
        foreach (var (status, count) in new[] { ("all", m.Assigned), ("not-submitted", m.NotSubmitted), ("submitted", m.Submitted),
                                                ("awaiting", m.AwaitingReview), ("graded", m.Graded), ("overdue", m.Overdue) })
        {
            m.WorkStatus = status;
            Assert.Equal(count, m.FilteredWork.Count);
        }
        m.WorkStatus = "all";
    }

    // ── The empty page, fixed ────────────────────────────────────────────────

    [Fact]
    public async Task Work_given_to_8_of_12_with_nothing_handed_in_is_shown_not_hidden()
    {
        var (t, cls, s) = await ClassAsync(12);
        var due = Now.AddDays(3);
        var a = await GiveAsync(cls, t.Id, s.Take(8), AssignmentType.ClassroomPractice, due);

        var m = (await BuildAsync(t.Id, cls.Id))!;

        Assert.Equal(8, m.Assigned);
        Assert.Equal(0, m.Submitted);
        Assert.Equal(8, m.NotSubmitted);
        Assert.Null(m.AverageScore);
        AssertAddsUp(m);
        var row = Assert.Single(m.Assignments);
        Assert.Equal((8, 0, due), (row.Recipients, row.Submitted, row.Deadline!.Value));
        // The four students who were not given it are not "not submitted": they have nothing assigned.
        m.WorkStatus = "not-submitted";
        Assert.DoesNotContain(m.FilteredWork, w => s.Skip(8).Any(x => x.Id == w.StudentId));
        Assert.Equal(4, m.Students.Count(x => x.Assigned == 0));
        Assert.Equal(12, m.Students.Count);
        // "Teacher actions" shows what is coming instead of nothing.
        var track = Assert.Single(m.Actions);
        Assert.Contains("0 of 8 submitted", track.Detail);
        Assert.Contains($"assignmentId={a.Id}", track.Href);
    }

    [Fact]
    public async Task Both_types_are_shown_by_default_and_old_links_still_filter_by_type()
    {
        var (t, cls, s) = await ClassAsync(2);
        await GiveAsync(cls, t.Id, s, AssignmentType.ClassroomPractice);
        await GiveAsync(cls, t.Id, s, AssignmentType.Homework);

        Assert.Equal(4, (await BuildAsync(t.Id, cls.Id))!.Assigned);

        var practice = (await BuildAsync(t.Id, cls.Id, tab: "practice"))!;
        Assert.Equal(("overview", "practice", 2), (practice.Tab, practice.Type, practice.Assigned));
        var assessment = (await BuildAsync(t.Id, cls.Id, tab: "assessment"))!;
        Assert.Equal(("overview", "homework", 2), (assessment.Tab, assessment.Type, assessment.Assigned));
    }

    // ── The control example: 24 given, 16 submitted (11 graded, 5 awaiting review), 8 not submitted ──

    [Fact]
    public async Task Control_example_numbers_agree_everywhere()
    {
        var (t, cls, s) = await ClassAsync(8);
        var a1 = await GiveAsync(cls, t.Id, s);
        var a2 = await GiveAsync(cls, t.Id, s);
        var a3 = await GiveAsync(cls, t.Id, s);
        // a1: 6 graded (scores 100, 80, 60, 40, 20, 0), 2 not submitted
        var scores = new[] { 100, 80, 60, 40, 20, 0 };
        for (var i = 0; i < 6; i++) await AttemptAsync(a1.Id, s[i].Id, 1, true, scores[i]);
        // a2: 5 graded at 50, 3 awaiting review (flagged)
        for (var i = 0; i < 5; i++) await AttemptAsync(a2.Id, s[i].Id, 1, true, 50);
        for (var i = 5; i < 8; i++) await AttemptAsync(a2.Id, s[i].Id, 1, true, 25, flag: true);
        // a3: 2 awaiting review (one flagged, one still with the AI), 6 not submitted (two of them with a draft)
        await AttemptAsync(a3.Id, s[0].Id, 1, true, 30, flag: true);
        await AttemptAsync(a3.Id, s[1].Id, 1, true, 0, q1: null, q2: null, aiPending: true);
        await AttemptAsync(a3.Id, s[2].Id, 1, false, 0);
        await AttemptAsync(a3.Id, s[3].Id, 1, false, 0);

        var m = (await BuildAsync(t.Id, cls.Id))!;

        Assert.Equal((24, 16, 11, 5, 8), (m.Assigned, m.Submitted, m.Graded, m.AwaitingReview, m.NotSubmitted));
        AssertAddsUp(m);
        // Average over the 11 graded: (100+80+60+40+20+0 + 5×50) / 11 = 550 / 11 = 50; the graded 0% counts.
        Assert.Equal(50, m.AverageScore);
        Assert.Equal(50, m.Assignments.Single(x => x.Id == a1.Id).AverageScore);
        Assert.Equal(6, m.Assignments.Single(x => x.Id == a1.Id).Graded);
        Assert.Null(m.Assignments.Single(x => x.Id == a3.Id).AverageScore);     // no grades ≠ 0%
        Assert.Contains(m.Actions, x => x.Href.Contains("status=awaiting") && x.Title.Contains("5"));
    }

    // ── Retakes, drafts, zero and partial marks ──────────────────────────────

    [Fact]
    public async Task A_retake_waiting_for_review_counts_once_and_a_draft_after_a_hand_in_does_not_undo_it()
    {
        var (t, cls, s) = await ClassAsync(3);
        var a = await GiveAsync(cls, t.Id, s);
        // s0: graded first attempt (90), second attempt handed in and waiting for the teacher.
        await AttemptAsync(a.Id, s[0].Id, 1, true, 90);
        await AttemptAsync(a.Id, s[0].Id, 2, true, 40, flag: true);
        // s1: graded 70, then opened a new draft.
        await AttemptAsync(a.Id, s[1].Id, 1, true, 70);
        await AttemptAsync(a.Id, s[1].Id, 2, false, 0);
        // s2: graded twice; the newest submitted attempt (60) is the one that counts, as in the gradebook.
        await AttemptAsync(a.Id, s[2].Id, 1, true, 100);
        await AttemptAsync(a.Id, s[2].Id, 2, true, 60);

        var m = (await BuildAsync(t.Id, cls.Id))!;

        Assert.Equal((3, 3, 1, 2), (m.Assigned, m.Submitted, m.AwaitingReview, m.Graded));
        AssertAddsUp(m);
        var w0 = m.Work.Single(w => w.StudentId == s[0].Id);
        Assert.Equal((AnalyticsWorkStatus.AwaitingReview, 2), (w0.Status, w0.SubmittedAttempts));
        var w1 = m.Work.Single(w => w.StudentId == s[1].Id);
        Assert.Equal((AnalyticsWorkStatus.Graded, 70, true), (w1.Status, w1.Score!.Value, w1.DraftOpen));
        Assert.Equal(60, m.Work.Single(w => w.StudentId == s[2].Id).Score);
        Assert.Equal(65, m.AverageScore);                                       // (70 + 60) / 2
    }

    [Fact]
    public async Task Questions_show_partial_marks_a_real_zero_and_how_many_answers_are_graded()
    {
        var (t, cls, s) = await ClassAsync(12);
        var a = await GiveAsync(cls, t.Id, s);
        await AttemptAsync(a.Id, s[0].Id, 1, true, 50, q1: 1, q2: 1.5m);       // partial
        await AttemptAsync(a.Id, s[1].Id, 1, true, 0, q1: 0, q2: 0);           // a real zero
        await AttemptAsync(a.Id, s[2].Id, 1, true, 25, q1: 1, flag: true);     // q2 waiting for the teacher

        var m = (await BuildAsync(t.Id, cls.Id, tab: "questions", questionAssignment: a.Id))!;

        Assert.Equal(2, m.Questions.Count);
        var q1 = m.Questions[0];
        Assert.Equal((1m, 3, 3), (q1.MaxPoints, q1.Answered, q1.GradedAnswers));
        Assert.Equal(0.67m, q1.AveragePoints);
        var q2 = m.Questions[1];
        Assert.Equal((3m, 3, 2), (q2.MaxPoints, q2.Answered, q2.GradedAnswers));   // "2 of 3 graded"
        Assert.Equal(0.75m, q2.AveragePoints);                                     // (1.5 + 0) / 2
        Assert.True(q2.Answers.Single(x => x.StudentId == s[2].Id).NeedsReview);
        Assert.Equal(0m, q2.Answers.Single(x => x.StudentId == s[1].Id).Points);
        // The zero counts in the average; the student who scored it is graded, not missing.
        Assert.Equal(AnalyticsWorkStatus.Graded, m.Work.Single(w => w.StudentId == s[1].Id).Status);
        Assert.Equal(25, m.AverageScore);                                          // (50 + 0) / 2
    }

    // ── Due dates, late joiners, the period ──────────────────────────────────

    [Fact]
    public async Task Overdue_uses_the_students_own_due_date_and_the_period_goes_by_when_each_got_the_work()
    {
        var (t, cls, s) = await ClassAsync(3);
        var a = await GiveAsync(cls, t.Id, s.Take(2), deadline: Now.AddDays(-1), assignedAt: Now.AddDays(-40));
        await using (var db = _db.NewContext())
        {
            // s1 has an extension; s2 joined late and was given the work 2 days ago.
            var ext = await db.AssignmentStudents.FirstAsync(x => x.AssignmentId == a.Id && x.StudentId == s[1].Id);
            ext.DueDateOverride = Now.AddDays(2);
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s[2].Id, AssignedAt = Now.AddDays(-2), DueDateOverride = Now.AddDays(5) });
            await db.SaveChangesAsync();
        }

        var all = (await BuildAsync(t.Id, cls.Id))!;
        Assert.Equal(3, all.Assigned);
        Assert.Equal(new[] { s[0].Id }, all.Work.Where(w => w.Overdue).Select(w => w.StudentId));
        AssertAddsUp(all);

        var week = (await BuildAsync(t.Id, cls.Id, period: "7d"))!;
        Assert.Equal(new[] { s[2].Id }, week.Work.Select(w => w.StudentId));
        Assert.True(week.Work[0].PersonalDue);
    }

    // ── Who may see it ───────────────────────────────────────────────────────

    [Fact]
    public async Task Owner_and_co_teacher_see_the_class_another_teacher_gets_nothing()
    {
        var (t, cls, s) = await ClassAsync(2);
        await GiveAsync(cls, t.Id, s);
        var co = await _db.UserAsync(Role.Teacher, "Co");
        var stranger = await _db.UserAsync(Role.Teacher, "Stranger");
        await using (var db = _db.NewContext())
        {
            db.Add(new ClassTeacher { ClassId = cls.Id, TeacherId = co.Id });
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, (await BuildAsync(t.Id, cls.Id))!.Assigned);
        Assert.Equal(2, (await BuildAsync(co.Id, cls.Id))!.Assigned);
        Assert.Null(await BuildAsync(stranger.Id, cls.Id));
        // Without a class in the address, a stranger only ever gets their own (none here).
        var own = (await BuildAsync(stranger.Id, null))!;
        Assert.Empty(own.Classes);
        Assert.Empty(own.Work);
    }

    [Fact]
    public async Task A_class_with_no_assignments_says_so()
    {
        var (t, cls, _) = await ClassAsync(3);
        var m = (await BuildAsync(t.Id, cls.Id))!;
        Assert.Equal((0, 0), (m.ClassAssignmentCount, m.Assigned));
        Assert.Equal(3, m.Students.Count);
    }
}
