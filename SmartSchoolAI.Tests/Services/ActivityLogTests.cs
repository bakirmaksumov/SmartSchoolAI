using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SmartSchoolAI.Tests.Services;

/// <summary>The student's actions per attempt: what is counted once, what goes before or after the hand-in, and size.</summary>
public class ActivityLogTests : IDisposable
{
    private readonly FileDb _db = new();
    private readonly ITestOutputHelper _out;
    private readonly IConfiguration _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ActivityLog:DetailRetentionDays"] = "30",
        ["ActivityLog:CollectingSince"] = "2026-11-01T00:00:00Z"
    }).Build();

    public ActivityLogTests(ITestOutputHelper output) => _out = output;
    public void Dispose() => _db.Dispose();

    private ActivityLogService Log(SmartSchoolAI.Data.AppDbContext db) => new(db, _config, NullLogger<ActivityLogService>.Instance);

    private async Task<(Guid student, Assignment a)> SetupAsync()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var s = await _db.UserAsync(Role.Student, "S");
        var cls = await _db.ClassAsync(t.Id, s.Id);
        var a = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        await using var db = _db.NewContext();
        db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s.Id });
        (await db.Assignments.FirstAsync(x => x.Id == a.Id)).MaxAttempts = 0;
        await db.SaveChangesAsync();
        return (s.Id, a);
    }

    private static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    private async Task<ActivityLogService.BatchResult> SendAsync(Guid student, Guid assignment, int attempt, params ActivityLogService.BrowserEvent[] events)
    {
        await using var db = _db.NewContext();
        return await Log(db).RecordBrowserBatchAsync(student, new(assignment, attempt, Ms(DateTime.UtcNow), events.ToList()));
    }

    private async Task<StudentResult> SubmitAsync(Guid student, Guid assignment, int attempt, DateTime at)
    {
        await using var db = _db.NewContext();
        var r = new StudentResult { AssignmentId = assignment, StudentId = student, AttemptNumber = attempt, IsSubmitted = true, SubmittedAt = at };
        db.StudentResults.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    private async Task<ActivityLogService.Timeline> TimelineAsync(Guid resultId, string? action = null, int? question = null)
    {
        await using var db = _db.NewContext();
        var r = await db.StudentResults.Include(x => x.ChatSessions).FirstAsync(x => x.Id == resultId);
        return await Log(db).TimelineAsync(r, action, question, 1);
    }

    [Fact]
    public async Task The_acceptance_scenario_counts_copy_1_paste_1_hints_1_of_2_tutor_1_after_submission()
    {
        var (s, a) = await SetupAsync();
        var t0 = DateTime.UtcNow.AddMinutes(-10);
        Assert.True((await SendAsync(s, a.Id, 1,
            new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(t0)),
            new ActivityLogService.BrowserEvent(Guid.NewGuid(), "paste", "blocked", 1, Ms(t0.AddMinutes(1))))).Ok);
        await using (var db = _db.NewContext())
        {
            var log = Log(db);
            await log.RecordAsync(a.Id, s, 1, 1, ActivityKind.HintRequested, ActivityOutcome.Received, Guid.NewGuid(), t0.AddMinutes(2));
            await log.RecordAsync(a.Id, s, 1, 1, ActivityKind.HintRequested, ActivityOutcome.Error, Guid.NewGuid(), t0.AddMinutes(3));
        }
        var result = await SubmitAsync(s, a.Id, 1, t0.AddMinutes(5));
        await using (var db = _db.NewContext())
            await Log(db).RecordAsync(a.Id, s, 1, null, ActivityKind.TutorMessage, ActivityOutcome.Received, null, t0.AddMinutes(7));

        var t = await TimelineAsync(result.Id);
        Assert.Equal((1, 1, 1, 1, 2, 1, 1), (t.Copies, t.Pastes, t.PastesBlocked, t.HintsReceived, t.HintsRequested, t.TutorRequests, t.TutorRequestsAfterSubmission));
        Assert.Equal(new[] { "Copy", "Paste", "Hint requested", "Hint requested", "Work submitted (attempt 1)", "AI Tutor message" }, t.Rows.Select(r => r.Action));
        Assert.Equal(new int?[] { 0, 1, 1, 1, null, null }, t.Rows.Select(r => r.Question));
        Assert.True(t.Rows.Last().AfterSubmission);
        Assert.All(t.Rows.Take(4), r => Assert.False(r.AfterSubmission));

        // Filters change the list and the totals together.
        var hints = await TimelineAsync(result.Id, action: "hint");
        Assert.Equal((0, 2, 1), (hints.Copies, hints.HintsRequested, hints.HintsReceived));
        Assert.Equal(2, hints.Rows.Count(r => !r.IsSubmission));
        var q0 = await TimelineAsync(result.Id, question: 0);
        Assert.Equal((1, 0, 0), (q0.Copies, q0.Pastes, q0.HintsRequested));
    }

    [Fact]
    public async Task A_batch_sent_again_after_a_lost_connection_is_stored_once_two_quick_presses_are_two()
    {
        var (s, a) = await SetupAsync();
        var now = DateTime.UtcNow;
        var batch = new[] { new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(now)),
                            new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(now.AddMilliseconds(80))) };
        var first = await SendAsync(s, a.Id, 1, batch);
        var again = await SendAsync(s, a.Id, 1, batch);

        Assert.Equal((2, 0), (first.Stored, first.Duplicates));
        Assert.Equal((0, 2), (again.Stored, again.Duplicates));
        await using var db = _db.NewContext();
        Assert.Equal(2, await db.AttemptActivities.CountAsync(x => x.StudentId == s));
    }

    [Fact]
    public async Task A_streamed_reply_is_one_request_and_its_outcome_is_the_last_known()
    {
        var (s, a) = await SetupAsync();
        var request = Guid.NewGuid();
        await using (var db = _db.NewContext())
        {
            var log = Log(db);
            await log.RecordAsync(a.Id, s, 1, 0, ActivityKind.TutorRequest, ActivityOutcome.LimitReached, request);  // refused by the limit
            await log.RecordAsync(a.Id, s, 1, 0, ActivityKind.TutorRequest, ActivityOutcome.Received, request);      // same request id
        }
        await using var check = _db.NewContext();
        var row = Assert.Single(await check.AttemptActivities.ToListAsync());
        Assert.Equal(ActivityOutcome.Received, row.Outcome);
    }

    [Fact]
    public async Task Only_the_students_own_attempts_and_only_browser_actions_are_accepted()
    {
        var (s, a) = await SetupAsync();
        var stranger = await _db.UserAsync(Role.Student, "Other");
        Assert.False((await SendAsync(stranger.Id, a.Id, 1, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(DateTime.UtcNow)))).Ok);
        Assert.False((await SendAsync(s, a.Id, 3, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(DateTime.UtcNow)))).Ok);   // no such attempt
        var r = await SendAsync(s, a.Id, 1, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "hint", null, 0, Ms(DateTime.UtcNow)));              // not from the browser
        Assert.Equal(0, r.Stored);
    }

    [Fact]
    public async Task A_paste_before_the_hand_in_stays_before_it_even_when_its_batch_arrives_later()
    {
        var (s, a) = await SetupAsync();
        var submittedAt = DateTime.UtcNow.AddMinutes(-2);
        var result = await SubmitAsync(s, a.Id, 1, submittedAt);
        // The paste happened a minute before the hand-in; the batch arrives now (after it).
        await SendAsync(s, a.Id, 1, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "paste", "allowed", 0, Ms(submittedAt.AddMinutes(-1))));

        var t = await TimelineAsync(result.Id);
        var paste = Assert.Single(t.Rows, r => r.Kind == ActivityKind.Paste);
        Assert.False(paste.AfterSubmission);
        Assert.Equal(new[] { "Paste", "Work submitted (attempt 1)" }, t.Rows.Select(r => r.Action));
    }

    [Fact]
    public async Task Two_attempts_keep_their_own_actions()
    {
        var (s, a) = await SetupAsync();
        var first = await SubmitAsync(s, a.Id, 1, DateTime.UtcNow.AddMinutes(-30));
        await SendAsync(s, a.Id, 2, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(DateTime.UtcNow)));
        var second = await SubmitAsync(s, a.Id, 2, DateTime.UtcNow.AddSeconds(5));

        Assert.Equal(0, (await TimelineAsync(first.Id)).Copies);
        Assert.Equal(1, (await TimelineAsync(second.Id)).Copies);
    }

    [Fact]
    public async Task Old_work_says_not_collected_an_empty_attempt_says_zero_and_purged_details_keep_their_totals()
    {
        var (s, a) = await SetupAsync();
        await using (var db = _db.NewContext())
        {
            db.StudentResults.Add(new StudentResult { AssignmentId = a.Id, StudentId = s, AttemptNumber = 1, IsSubmitted = true,
                                                      CreatedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) });
            await db.SaveChangesAsync();
        }
        var old = await (_db.NewContext()).StudentResults.FirstAsync(r => r.AttemptNumber == 1);
        Assert.Equal("not-collected", (await TimelineAsync(old.Id)).State);

        var fresh = await SubmitAsync(s, a.Id, 2, DateTime.UtcNow);
        Assert.Equal("none", (await TimelineAsync(fresh.Id)).State);

        await SendAsync(s, a.Id, 2, new ActivityLogService.BrowserEvent(Guid.NewGuid(), "copy", null, 0, Ms(DateTime.UtcNow)));
        await using (var db = _db.NewContext())
            Assert.Equal(1, await Log(db).PurgeAsync(DateTime.UtcNow.AddDays(31)));
        var after = await TimelineAsync(fresh.Id);
        Assert.Equal("purged", after.State);
        Assert.Equal(1, after.Summary!.Copies);
    }

    [Fact]
    public async Task Ten_thousand_actions_take_little_space()
    {
        var (s, a) = await SetupAsync();
        long Size()
        {
            using var c = _db.Open();
            using (var cp = c.CreateCommand()) { cp.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);"; cp.ExecuteNonQuery(); }
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA page_count;";
            var pages = (long)cmd.ExecuteScalar()!;
            cmd.CommandText = "PRAGMA page_size;";
            return pages * (long)cmd.ExecuteScalar()!;
        }
        using (var v = _db.Open()) { using var cmd = v.CreateCommand(); cmd.CommandText = "VACUUM;"; cmd.ExecuteNonQuery(); }
        var before = Size();

        await using (var db = _db.NewContext())
        {
            var now = DateTime.UtcNow;
            var rnd = new Random(1);
            for (var i = 0; i < 10_000; i++)
                db.AttemptActivities.Add(new AttemptActivity
                {
                    AssignmentId = a.Id, StudentId = s, AttemptNumber = 1 + i / 2500, QuestionIndex = (short)(i % 12),
                    Kind = (byte)(1 + rnd.Next(7)), Outcome = (byte)(1 + rnd.Next(6)), OccurredAt = now.AddSeconds(i),
                    ReceivedAt = now.AddSeconds(i + 2), ClientId = i % 3 == 0 ? null : Guid.NewGuid(), Flags = 1
                });
            await db.SaveChangesAsync();
        }
        using (var v = _db.Open()) { using var cmd = v.CreateCommand(); cmd.CommandText = "VACUUM;"; cmd.ExecuteNonQuery(); }
        var growth = Size() - before;

        _out.WriteLine($"10,000 actions (2/3 with a client id): +{growth:N0} bytes ({growth / 10_000.0:N0} bytes per action, table and indexes, after VACUUM)");
        Assert.InRange(growth, 1, 3_000_000);
    }
}
