using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// Giving work out (one atomic action), the late-join rule, and approving requests — against a real SQLite file,
/// with separate connections for concurrent requests, so locks and unique keys are the production ones.
/// </summary>
public class AssignmentDistributionTests : IDisposable
{
    private readonly FileDb _db = new();
    private readonly FakeWebHostEnvironment _env = new();

    public void Dispose()
    {
        _db.Dispose();
        _env.Dispose();
    }

    private AssignmentDistributionService Service(SmartSchoolAI.Data.AppDbContext db)
        => new(db, NullLogger<AssignmentDistributionService>.Instance);

    private static AssignmentDistributionService.AssignRequest Entire(LateJoinPolicy p = LateJoinPolicy.AutoAssign, DateTime? deadline = null)
        => new(AssignmentAudience.EntireClass, Array.Empty<Guid>(), p, null, deadline);

    private static AssignmentDistributionService.AssignRequest Selected(params Guid[] ids)
        => new(AssignmentAudience.SelectedStudents, ids, LateJoinPolicy.Manual, null, null);

    private async Task<List<Guid>> RecipientsAsync(Guid assignmentId)
    {
        await using var db = _db.NewContext();
        return await db.AssignmentStudents.Where(x => x.AssignmentId == assignmentId).Select(x => x.StudentId).ToListAsync();
    }

    private async Task<Assignment> ReloadAsync(Guid id)
    {
        await using var db = _db.NewContext();
        return await db.Assignments.AsNoTracking().FirstAsync(a => a.Id == id);
    }

    // ── One action: recipients + publishing ──────────────────────────────────

    [Fact]
    public async Task Assign_EntireClass_GivesEveryMemberAndPublishes()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var s = new[] { await _db.UserAsync(Role.Student, "A"), await _db.UserAsync(Role.Student, "B"), await _db.UserAsync(Role.Student, "C") };
        var cls = await _db.ClassAsync(t.Id, s.Select(x => x.Id).ToArray());
        var a = await _db.AssignmentAsync(cls.Id, t.Id);

        await using var db = _db.NewContext();
        var outcome = await Service(db).AssignAsync(a.Id, t.Id, Entire());

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(3, outcome.Recipients);
        Assert.Equal(cls.Name, outcome.ClassName);
        var saved = await ReloadAsync(a.Id);
        Assert.Equal(AssignmentStatus.Active, saved.Status);
        Assert.Equal(AssignmentAudience.EntireClass, saved.Audience);
        Assert.Equal(LateJoinPolicy.AutoAssign, saved.LateJoinPolicy);
        Assert.Equal(s.Select(x => x.Id).OrderBy(x => x), (await RecipientsAsync(a.Id)).OrderBy(x => x));
    }

    [Fact]
    public async Task Assign_SelectedOnDraft_ReplacesTheEarlierChoiceExactly()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var a1 = await _db.UserAsync(Role.Student, "A"); var b = await _db.UserAsync(Role.Student, "B"); var c = await _db.UserAsync(Role.Student, "C");
        var cls = await _db.ClassAsync(t.Id, a1.Id, b.Id, c.Id);
        var a = await _db.AssignmentAsync(cls.Id, t.Id);
        await using (var db = _db.NewContext())
        {
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = a1.Id });
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = b.Id });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext())
        {
            var outcome = await Service(db).AssignAsync(a.Id, t.Id, Selected(b.Id, c.Id));
            Assert.True(outcome.Success, outcome.Error);
            Assert.Equal(2, outcome.Recipients);
        }

        Assert.Equal(new[] { b.Id, c.Id }.OrderBy(x => x), (await RecipientsAsync(a.Id)).OrderBy(x => x));
        var saved = await ReloadAsync(a.Id);
        Assert.Equal(AssignmentAudience.SelectedStudents, saved.Audience);
        Assert.Equal(LateJoinPolicy.Manual, saved.LateJoinPolicy);
    }

    [Fact]
    public async Task Assign_WhenPublishingFails_ChangesNothing()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var s1 = await _db.UserAsync(Role.Student, "A"); var s2 = await _db.UserAsync(Role.Student, "B");
        var cls = await _db.ClassAsync(t.Id, s1.Id, s2.Id);
        var a = await _db.AssignmentAsync(cls.Id, t.Id, AssignmentType.Homework);   // homework, no deadline
        await using (var db = _db.NewContext())
        {
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s1.Id });
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext())
        {
            var outcome = await Service(db).AssignAsync(a.Id, t.Id, Selected(s2.Id));
            Assert.False(outcome.Success);
            Assert.Contains("deadline", outcome.Error);
            Assert.Empty(db.ChangeTracker.Entries());
        }

        var saved = await ReloadAsync(a.Id);
        Assert.Equal(AssignmentStatus.Draft, saved.Status);
        Assert.Equal(new[] { s1.Id }, await RecipientsAsync(a.Id));          // the earlier choice is untouched
    }

    [Fact]
    public async Task Assign_RefusesStudentsOutsideTheClassAndClosedWork()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var inside = await _db.UserAsync(Role.Student, "In"); var outside = await _db.UserAsync(Role.Student, "Out");
        var cls = await _db.ClassAsync(t.Id, inside.Id);
        var draft = await _db.AssignmentAsync(cls.Id, t.Id);
        var closed = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Closed);

        await using var db = _db.NewContext();
        var foreign = await Service(db).AssignAsync(draft.Id, t.Id, Selected(inside.Id, outside.Id));
        Assert.False(foreign.Success);
        Assert.Contains("no longer in", foreign.Error);

        var shut = await Service(db).AssignAsync(closed.Id, t.Id, Entire());
        Assert.False(shut.Success);
        Assert.Contains("closed", shut.Error);

        Assert.Empty(await RecipientsAsync(draft.Id));
        Assert.Equal(AssignmentStatus.Draft, (await ReloadAsync(draft.Id)).Status);
    }

    [Fact]
    public async Task Assign_ByAnotherTeacher_IsNotFound_ButACoTeacherMayAssign()
    {
        var owner = await _db.UserAsync(Role.Teacher, "Owner");
        var co = await _db.UserAsync(Role.Teacher, "Co");
        var stranger = await _db.UserAsync(Role.Teacher, "Stranger");
        var s = await _db.UserAsync(Role.Student, "S");
        var cls = await _db.ClassAsync(owner.Id, s.Id);
        await using (var db = _db.NewContext())
        {
            db.ClassTeachers.Add(new ClassTeacher { ClassId = cls.Id, TeacherId = co.Id });
            await db.SaveChangesAsync();
        }
        var a = await _db.AssignmentAsync(cls.Id, owner.Id);

        await using (var db = _db.NewContext())
            Assert.False((await Service(db).AssignAsync(a.Id, stranger.Id, Entire())).Success);
        Assert.Empty(await RecipientsAsync(a.Id));

        await using (var db = _db.NewContext())
            Assert.True((await Service(db).AssignAsync(a.Id, co.Id, Entire())).Success);
        Assert.Single(await RecipientsAsync(a.Id));
    }

    [Fact]
    public async Task Assign_TwiceAtOnce_StoresEachRecipientOnce()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var students = new List<Guid>();
        for (var i = 0; i < 5; i++) students.Add((await _db.UserAsync(Role.Student, "S" + i)).Id);
        var cls = await _db.ClassAsync(t.Id, students.ToArray());
        var a = await _db.AssignmentAsync(cls.Id, t.Id);

        // A double click: two identical requests on two connections.
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            await using var db = _db.NewContext();
            return await Service(db).AssignAsync(a.Id, t.Id, Entire());
        }));

        Assert.Contains(results, r => r.Success);
        Assert.Equal(5, (await RecipientsAsync(a.Id)).Count);
        Assert.Equal(5, (await RecipientsAsync(a.Id)).Distinct().Count());
        Assert.Equal(AssignmentStatus.Active, (await ReloadAsync(a.Id)).Status);
    }

    // ── Students who join later ──────────────────────────────────────────────

    private async Task<(bool ok, string error)> JoinAsync(Guid studentId, string code)
    {
        await using var db = _db.NewContext();
        return await new AuthService(db, _env).JoinClassAsync(studentId, code);
    }

    [Fact]
    public async Task Join_AppliesEachAssignmentsRule_AndNeverExtendsHandPickedClosedOrPastDueWork()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var first = await _db.UserAsync(Role.Student, "First");
        var cls = await _db.ClassAsync(t.Id, first.Id);
        var now = DateTime.UtcNow;

        var auto     = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active, audience: AssignmentAudience.EntireClass, policy: LateJoinPolicy.AutoAssign);
        var approval = await _db.AssignmentAsync(cls.Id, t.Id, AssignmentType.Homework, AssignmentStatus.Active, now.AddDays(3), AssignmentAudience.EntireClass, LateJoinPolicy.TeacherApproval);
        var picked   = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        var closed   = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Closed, audience: AssignmentAudience.EntireClass, policy: LateJoinPolicy.AutoAssign);
        var draft    = await _db.AssignmentAsync(cls.Id, t.Id, audience: AssignmentAudience.EntireClass, policy: LateJoinPolicy.AutoAssign);
        var pastDue  = await _db.AssignmentAsync(cls.Id, t.Id, AssignmentType.Homework, AssignmentStatus.Active, now.AddDays(-1), AssignmentAudience.EntireClass, LateJoinPolicy.AutoAssign);

        var newcomer = await _db.UserAsync(Role.Student, "New");
        var (ok, error) = await JoinAsync(newcomer.Id, cls.InviteCode);
        Assert.True(ok, error);

        await using var db = _db.NewContext();
        var given = await db.AssignmentStudents.Where(x => x.StudentId == newcomer.Id).Select(x => x.AssignmentId).ToListAsync();
        Assert.Equal(new[] { auto.Id }, given);
        var requests = await db.LateJoinRequests.Where(r => r.StudentId == newcomer.Id).ToListAsync();
        var request = Assert.Single(requests);
        Assert.Equal(approval.Id, request.AssignmentId);
        Assert.Equal(LateJoinRequestStatus.Pending, request.Status);
        _ = (picked, closed, draft, pastDue);
    }

    [Fact]
    public async Task Join_Twice_AtTheSameMoment_MakesOneMembershipAndNoError()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var cls = await _db.ClassAsync(t.Id);
        var auto = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active, audience: AssignmentAudience.EntireClass, policy: LateJoinPolicy.AutoAssign);
        var approval = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active, audience: AssignmentAudience.EntireClass, policy: LateJoinPolicy.TeacherApproval);
        var s = await _db.UserAsync(Role.Student, "S");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => JoinAsync(s.Id, cls.InviteCode)));

        Assert.Single(results, r => r.ok);
        Assert.All(results.Where(r => !r.ok), r => Assert.Contains("already enrolled", r.error));
        await using var db = _db.NewContext();
        Assert.Equal(1, await db.ClassStudents.CountAsync(cs => cs.ClassId == cls.Id && cs.StudentId == s.Id));
        Assert.Equal(1, await db.AssignmentStudents.CountAsync(x => x.AssignmentId == auto.Id && x.StudentId == s.Id));
        Assert.Equal(1, await db.LateJoinRequests.CountAsync(r => r.AssignmentId == approval.Id && r.StudentId == s.Id));
    }

    [Theory]
    [InlineData(LateJoinPolicy.AutoAssign)]
    [InlineData(LateJoinPolicy.TeacherApproval)]
    public async Task JoinWhileTheTeacherAssigns_TheNewcomerIsNeverMissedAndNeverDoubled(LateJoinPolicy policy)
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        for (var round = 0; round < 12; round++)
        {
            var member = await _db.UserAsync(Role.Student, "M");
            var cls = await _db.ClassAsync(t.Id, member.Id);
            var a = await _db.AssignmentAsync(cls.Id, t.Id);
            var newcomer = await _db.UserAsync(Role.Student, "N");

            var assign = Task.Run(async () =>
            {
                await using var db = _db.NewContext();
                return await Service(db).AssignAsync(a.Id, t.Id, Entire(policy));
            });
            var join = Task.Run(() => JoinAsync(newcomer.Id, cls.InviteCode));
            var assigned0 = await assign;
            var joined0   = await join;
            Assert.True(assigned0.Success, assigned0.Error);
            Assert.True(joined0.ok, joined0.error);

            await using var check = _db.NewContext();
            var assigned  = await check.AssignmentStudents.CountAsync(x => x.AssignmentId == a.Id && x.StudentId == newcomer.Id);
            var requested = await check.LateJoinRequests.CountAsync(r => r.AssignmentId == a.Id && r.StudentId == newcomer.Id && r.Status == LateJoinRequestStatus.Pending);

            // Either a member when the teacher assigned (given directly), or a late joiner under the rule —
            // exactly one of the two, whichever order the two requests ran in.
            Assert.Equal(1, assigned + requested);
            if (policy == LateJoinPolicy.AutoAssign) Assert.Equal(1, assigned);
        }
    }

    [Fact]
    public async Task OldAssignments_AreLeftAlone_UntilTheTeacherSwitchesTheRuleOn()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var a1 = await _db.UserAsync(Role.Student, "A"); var a2 = await _db.UserAsync(Role.Student, "B"); var a3 = await _db.UserAsync(Role.Student, "C");
        var cls = await _db.ClassAsync(t.Id, a1.Id, a2.Id, a3.Id);
        // An assignment from before: active, given to all three by hand — the defaults the migration gives it.
        var old = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        await using (var db = _db.NewContext())
        {
            foreach (var s in new[] { a1, a2, a3 }) db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = old.Id, StudentId = s.Id });
            await db.SaveChangesAsync();
        }

        var fourth = await _db.UserAsync(Role.Student, "D");
        Assert.True((await JoinAsync(fourth.Id, cls.InviteCode)).ok);
        Assert.Equal(3, (await RecipientsAsync(old.Id)).Count);              // all three had it: still no proof for a fourth

        await using (var db = _db.NewContext())
            Assert.True(await Service(db).SetLateJoinPolicyAsync(old.Id, t.Id, LateJoinPolicy.AutoAssign));
        Assert.Equal(3, (await RecipientsAsync(old.Id)).Count);              // switching it on adds nobody already there

        var fifth = await _db.UserAsync(Role.Student, "E");
        Assert.True((await JoinAsync(fifth.Id, cls.InviteCode)).ok);
        var now = await RecipientsAsync(old.Id);
        Assert.Contains(fifth.Id, now);
        Assert.DoesNotContain(fourth.Id, now);
    }

    // ── Approving and declining ──────────────────────────────────────────────

    private async Task<(User teacher, User co, Class cls, Assignment a, LateJoinRequest request, User student)> PendingSetupAsync(DateTime? deadline = null)
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var co = await _db.UserAsync(Role.Teacher, "Co");
        var cls = await _db.ClassAsync(t.Id);
        await using (var db = _db.NewContext())
        {
            db.ClassTeachers.Add(new ClassTeacher { ClassId = cls.Id, TeacherId = co.Id });
            await db.SaveChangesAsync();
        }
        var a = await _db.AssignmentAsync(cls.Id, t.Id, AssignmentType.Homework, AssignmentStatus.Active,
            deadline ?? DateTime.UtcNow.AddDays(5), AssignmentAudience.EntireClass, LateJoinPolicy.TeacherApproval);
        var s = await _db.UserAsync(Role.Student, "Late");
        Assert.True((await JoinAsync(s.Id, cls.InviteCode)).ok);
        await using var read = _db.NewContext();
        var request = await read.LateJoinRequests.SingleAsync(r => r.StudentId == s.Id);
        Assert.Empty(await RecipientsAsync(a.Id));                         // waiting: no access yet
        return (t, co, cls, a, request, s);
    }

    [Fact]
    public async Task Approve_ByTwoTeachersAtOnce_GivesTheWorkOnce()
    {
        var (t, co, cls, a, request, s) = await PendingSetupAsync();

        var outcomes = await Task.WhenAll(new[] { t.Id, co.Id }.Select(async teacherId =>
        {
            await using var db = _db.NewContext();
            return await Service(db).ApproveAsync(cls.Id, teacherId, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
        }));

        Assert.Equal(1, outcomes.Sum(o => o.Approved));
        Assert.Equal(1, outcomes.Sum(o => o.Skipped));
        Assert.Equal(new[] { s.Id }, await RecipientsAsync(a.Id));
    }

    [Fact]
    public async Task ApproveAndDeclineAtOnce_EndInOneDecision()
    {
        var (t, co, cls, a, request, _) = await PendingSetupAsync();

        var approve = Task.Run(async () =>
        {
            await using var db = _db.NewContext();
            return await Service(db).ApproveAsync(cls.Id, t.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
        });
        var decline = Task.Run(async () =>
        {
            await using var db = _db.NewContext();
            return await Service(db).DeclineAsync(cls.Id, co.Id, new[] { request.Id });
        });
        var approved = await approve;
        var declined = await decline;

        await using var check = _db.NewContext();
        var final = await check.LateJoinRequests.SingleAsync(r => r.Id == request.Id);
        var given = (await RecipientsAsync(a.Id)).Count;
        Assert.Equal(1, approved.Approved + declined.Declined);
        Assert.Equal(final.Status == LateJoinRequestStatus.Approved ? 1 : 0, given);
    }

    [Fact]
    public async Task Approve_ReChecksAtTheMomentOfApproval()
    {
        // Closed in the meantime.
        var (t, _, cls, a, request, _) = await PendingSetupAsync();
        await using (var db = _db.NewContext())
        {
            (await db.Assignments.FirstAsync(x => x.Id == a.Id)).Status = AssignmentStatus.Closed;
            await db.SaveChangesAsync();
        }
        await using (var db = _db.NewContext())
        {
            var outcome = await Service(db).ApproveAsync(cls.Id, t.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
            Assert.Equal(0, outcome.Approved);
            Assert.Contains(outcome.Notes, n => n.Contains("no longer active"));
        }
        Assert.Empty(await RecipientsAsync(a.Id));
        await using (var db = _db.NewContext())
            Assert.Equal(LateJoinRequestStatus.Pending, (await db.LateJoinRequests.SingleAsync(r => r.Id == request.Id)).Status);
    }

    [Fact]
    public async Task Approve_PastDue_OnlyWithALaterPersonalDueDate()
    {
        var (t, _, cls, a, request, s) = await PendingSetupAsync();
        await using (var db = _db.NewContext())
        {
            (await db.Assignments.FirstAsync(x => x.Id == a.Id)).Deadline = DateTime.UtcNow.AddHours(-2);
            await db.SaveChangesAsync();
        }

        await using (var db = _db.NewContext())
            Assert.Equal(0, (await Service(db).ApproveAsync(cls.Id, t.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) })).Approved);

        var personal = DateTime.UtcNow.AddDays(2);
        await using (var db = _db.NewContext())
            Assert.Equal(1, (await Service(db).ApproveAsync(cls.Id, t.Id, new[] { new AssignmentDistributionService.Decision(request.Id, personal) })).Approved);

        await using var check = _db.NewContext();
        var row = await check.AssignmentStudents.SingleAsync(x => x.AssignmentId == a.Id && x.StudentId == s.Id);
        Assert.Equal(personal, row.DueDateOverride!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Approve_ForAStudentWhoLeftTheClass_IsSkipped_AndForeignRequestsAreIgnored()
    {
        var (t, _, cls, a, request, s) = await PendingSetupAsync();
        var otherTeacher = await _db.UserAsync(Role.Teacher, "Other");
        var otherClass = await _db.ClassAsync(otherTeacher.Id);

        await using (var db = _db.NewContext())
        {
            // A request id from this class sent with another class's id is not touched.
            var foreign = await Service(db).ApproveAsync(otherClass.Id, otherTeacher.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
            Assert.Equal(0, foreign.Approved);
            // And a teacher without access to this class gets nothing done either.
            var noAccess = await Service(db).ApproveAsync(cls.Id, otherTeacher.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
            Assert.Equal(0, noAccess.Approved);
        }

        await using (var db = _db.NewContext())
        {
            db.ClassStudents.Remove(await db.ClassStudents.SingleAsync(cs => cs.ClassId == cls.Id && cs.StudentId == s.Id));
            await db.SaveChangesAsync();
        }
        await using (var db = _db.NewContext())
        {
            var outcome = await Service(db).ApproveAsync(cls.Id, t.Id, new[] { new AssignmentDistributionService.Decision(request.Id, null) });
            Assert.Equal(0, outcome.Approved);
            Assert.Contains(outcome.Notes, n => n.Contains("left the class"));
            Assert.Empty(await Service(db).PendingForClassAsync(cls.Id, t.Id));
        }
        Assert.Empty(await RecipientsAsync(a.Id));
    }

    // ── The review queue ─────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewQueue_CountsSubmittedFlaggedWork_OfThisTeachersClassesOnly()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var other = await _db.UserAsync(Role.Teacher, "Other");
        var s = await _db.UserAsync(Role.Student, "S");
        var mine = await _db.ClassAsync(t.Id, s.Id);
        var theirs = await _db.ClassAsync(other.Id, s.Id);
        var a = await _db.AssignmentAsync(mine.Id, t.Id, status: AssignmentStatus.Active);
        var b = await _db.AssignmentAsync(theirs.Id, other.Id, status: AssignmentStatus.Active);

        await using (var db = _db.NewContext())
        {
            StudentResult Result(Guid assignmentId, int attempt, bool submitted, bool flagged) => new()
            {
                AssignmentId = assignmentId, StudentId = s.Id, AttemptNumber = attempt, IsSubmitted = submitted,
                StudentAnswers = { new StudentAnswer { QuestionIndex = 0, NeedsReview = flagged } }
            };
            db.StudentResults.AddRange(
                Result(a.Id, 1, submitted: true,  flagged: true),    // counts
                Result(a.Id, 2, submitted: true,  flagged: false),   // nothing to check
                Result(a.Id, 3, submitted: false, flagged: true),    // still a draft
                Result(b.Id, 1, submitted: true,  flagged: true));   // another teacher's
            await db.SaveChangesAsync();
        }

        await using var read = _db.NewContext();
        var queue = await AssignmentsController.ReviewQueueQuery(read, t.Id).ToListAsync();
        var only = Assert.Single(queue);
        Assert.Equal(1, only.AttemptNumber);
        Assert.Equal(a.Id, only.AssignmentId);
    }
}
