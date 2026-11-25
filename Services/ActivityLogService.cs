using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// The student's actions on one attempt, for the teacher's "Activity" tab.
///
/// Recorded here (nothing else):
///   * copy / cut / paste the browser reports inside the assignment, with whether the page allowed or blocked it;
///   * opening the AI tutor while working (an explicit click; a reload or a restored conversation is not an opening);
///   * a hint request (the tutor opened for a question, or its Hint button) — one request id until its result;
///   * a tutor request (Concept / Solution) and a message the student sent — with the outcome the server saw:
///     answered, limit reached, refused, error, interrupted.
/// Read from existing records rather than stored again: the hand-in (the attempt's SubmittedAt) and opening the
/// tutor after the hand-in (the chat session's creation, one per mode, so a reload never counts twice).
/// Never stored: clipboard contents, selected text, keystrokes, the prompts or replies.
/// Logging must never get in the way of the work: every write here swallows its own failures.
/// </summary>
public class ActivityLogService
{
    public const int MaxBatch = 50;
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(10);
    /// <summary>A browser action this close to the hand-in may be on either side of it.</summary>
    public static readonly TimeSpan BoundaryUncertainty = TimeSpan.FromSeconds(3);

    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<ActivityLogService> _logger;

    public ActivityLogService(AppDbContext db, IConfiguration config, ILogger<ActivityLogService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>Attempts started before this time have no activity log (the log did not exist yet).</summary>
    public DateTime CollectingSince => DateTime.TryParse(_config["ActivityLog:CollectingSince"], System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
        ? d : new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>How long the detailed actions are kept (ActivityLog:DetailRetentionDays, default 365); totals stay.</summary>
    public int RetentionDays => Math.Max(7, _config.GetValue("ActivityLog:DetailRetentionDays", 365));

    // ── Browser actions ──────────────────────────────────────────────────────

    public sealed record BrowserEvent(Guid Id, string Kind, string? Outcome, int? Question, long At);
    public sealed record BrowserBatch(Guid AssignmentId, int Attempt, long SentAt, List<BrowserEvent> Events);
    public sealed record BatchResult(bool Ok, int Stored, int Duplicates, string? Error = null);

    /// <summary>
    /// Stores a batch of browser actions for the student's own attempt. Only copy / cut / paste and opening the tutor
    /// are accepted from the browser; a batch delivered twice (a retry after a lost connection) stores nothing twice.
    /// The browser's clock is corrected by the difference between its "sent at" and the server's time.
    /// </summary>
    public async Task<BatchResult> RecordBrowserBatchAsync(Guid studentId, BrowserBatch batch)
    {
        try
        {
            if (batch.Events is null || batch.Events.Count == 0) return new(true, 0, 0);
            if (batch.Events.Count > MaxBatch) return new(false, 0, 0, "Too many actions in one batch.");

            // Only the student's own attempts at work they were given: the one in progress, or one already handed in.
            if (!await _db.AssignmentStudents.AnyAsync(x => x.AssignmentId == batch.AssignmentId && x.StudentId == studentId))
                return new(false, 0, 0, "Not your assignment.");
            var submitted = await _db.StudentResults.CountAsync(r => r.AssignmentId == batch.AssignmentId && r.StudentId == studentId && r.IsSubmitted);
            if (batch.Attempt < 1 || batch.Attempt > submitted + 1) return new(false, 0, 0, "Unknown attempt.");

            var now = DateTime.UtcNow;
            var sent = DateTimeOffset.FromUnixTimeMilliseconds(batch.SentAt).UtcDateTime;
            var skew = now - sent;
            var skewKnown = skew.Duration() <= MaxClockSkew;

            var ids = batch.Events.Select(e => (Guid?)e.Id).ToList();
            var known = (await _db.AttemptActivities.Where(a => a.StudentId == studentId && ids.Contains(a.ClientId))
                                  .Select(a => a.ClientId).ToListAsync()).ToHashSet();
            var stored = 0;
            foreach (var e in batch.Events.DistinctBy(e => e.Id))
            {
                if (known.Contains(e.Id)) continue;
                var kind = e.Kind switch { "copy" => ActivityKind.Copy, "cut" => ActivityKind.Cut, "paste" => ActivityKind.Paste,
                                           "tutor-open" => ActivityKind.TutorOpened, _ => (byte)0 };
                if (kind == 0) continue;                                  // nothing else may come from the browser
                var outcome = kind == ActivityKind.Paste && e.Outcome == "blocked" ? ActivityOutcome.Blocked : ActivityOutcome.Allowed;
                var at = DateTimeOffset.FromUnixTimeMilliseconds(e.At).UtcDateTime;
                var occurred = skewKnown ? at + skew : now;
                if (occurred > now) occurred = now;
                _db.AttemptActivities.Add(new AttemptActivity
                {
                    AssignmentId = batch.AssignmentId, StudentId = studentId, AttemptNumber = batch.Attempt,
                    QuestionIndex = e.Question is >= 0 and < 500 ? (short)e.Question : null,
                    Kind = kind, Outcome = outcome, OccurredAt = occurred, ReceivedAt = now, ClientId = e.Id,
                    Flags = (byte)(ActivityFlags.FromBrowser | (skewKnown ? ActivityFlags.None : ActivityFlags.OrderUncertain))
                });
                stored++;
            }
            await _db.SaveChangesAsync();
            return new(true, stored, batch.Events.Count - stored);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning("Activity batch not stored ({Type})", ex.GetType().Name);
            return new(false, 0, 0, "Not stored.");
        }
    }

    // ── Server-side actions ──────────────────────────────────────────────────

    /// <summary>
    /// A tutor action with its server-side outcome. With a request id, the same request is one action: a later result
    /// for it (a retry, the end of the stream) updates the outcome instead of adding a second action.
    /// </summary>
    public async Task RecordAsync(Guid assignmentId, Guid studentId, int attempt, int? question, byte kind, byte outcome,
                                  Guid? requestId = null, DateTime? occurredAt = null)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (requestId is { } rid)
            {
                var existing = await _db.AttemptActivities.FirstOrDefaultAsync(a => a.StudentId == studentId && a.ClientId == rid);
                if (existing is not null)
                {
                    existing.Outcome = outcome;
                    await _db.SaveChangesAsync();
                    return;
                }
            }
            _db.AttemptActivities.Add(new AttemptActivity
            {
                AssignmentId = assignmentId, StudentId = studentId, AttemptNumber = Math.Max(1, attempt),
                QuestionIndex = question is >= 0 and < 500 ? (short)question : null,
                Kind = kind, Outcome = outcome, OccurredAt = occurredAt ?? now, ReceivedAt = now, ClientId = requestId
            });
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogWarning("Activity not stored ({Type})", ex.GetType().Name);
        }
    }

    /// <summary>The attempt a student is on: the one in progress (handed-in attempts + 1). Used for the tutor while working.</summary>
    public async Task<int> CurrentAttemptAsync(Guid assignmentId, Guid studentId)
        => await _db.StudentResults.CountAsync(r => r.AssignmentId == assignmentId && r.StudentId == studentId && r.IsSubmitted) + 1;

    // ── For the teacher ──────────────────────────────────────────────────────

    public sealed record Row(DateTime At, string Action, string Icon, byte Kind, int? Question, string Outcome, string Tone,
                             bool AfterSubmission, bool OrderUncertain, bool FromBrowser, bool IsSubmission = false);

    public sealed class Timeline
    {
        /// <summary>none | not-collected | purged | events</summary>
        public string State { get; init; } = "events";
        public List<Row> Rows { get; init; } = new();          // after the filters, all of them (counts come from here)
        public List<Row> PageRows { get; init; } = new();
        public int Page { get; init; } = 1;
        public int TotalPages { get; init; } = 1;
        public int Copies { get; init; }
        public int Pastes { get; init; }
        public int PastesBlocked { get; init; }
        public int HintsRequested { get; init; }
        public int HintsReceived { get; init; }
        public int TutorOpened { get; init; }
        public int TutorRequests { get; init; }
        public Dictionary<string, int> TutorOutcomes { get; init; } = new();
        public int TutorRequestsAfterSubmission { get; init; }
        public AttemptActivitySummary? Summary { get; init; }
        public List<int> Questions { get; init; } = new();
        public string? Action { get; init; }
        public int? Question { get; init; }
    }

    public const int PageSize = 50;

    /// <summary>
    /// The attempt's actions in time order, with the hand-in as the boundary between the work and what came after.
    /// Filters (action, question) apply to the list and to the totals alike, so the numbers always match the rows.
    /// One query for the actions, one for the totals kept after clean-up; the chat sessions come with the attempt.
    /// </summary>
    public async Task<Timeline> TimelineAsync(StudentResult attempt, string? action, int? question, int page)
    {
        var events = await _db.AttemptActivities.AsNoTracking()
            .Where(a => a.AssignmentId == attempt.AssignmentId && a.StudentId == attempt.StudentId && a.AttemptNumber == attempt.AttemptNumber)
            .OrderBy(a => a.OccurredAt).ThenBy(a => a.Id)
            .ToListAsync();
        var summary = await _db.AttemptActivitySummaries.AsNoTracking()
            .FirstOrDefaultAsync(s => s.AssignmentId == attempt.AssignmentId && s.StudentId == attempt.StudentId && s.AttemptNumber == attempt.AttemptNumber);

        DateTime? submittedAt = attempt.IsSubmitted ? attempt.SubmittedAt : null;
        bool After(DateTime t) => submittedAt is { } s && t > s;
        bool Near(DateTime t) => submittedAt is { } s && (t - s).Duration() <= BoundaryUncertainty;

        var rows = events.Select(e =>
        {
            var browser = (e.Flags & (byte)ActivityFlags.FromBrowser) != 0;
            var (label, icon) = e.Kind switch
            {
                ActivityKind.Copy => ("Copy", "bx-copy"), ActivityKind.Cut => ("Cut", "bx-cut"), ActivityKind.Paste => ("Paste", "bx-paste"),
                ActivityKind.HintRequested => ("Hint requested", "bx-bulb"), ActivityKind.TutorOpened => ("AI Tutor opened", "bx-message-rounded"),
                ActivityKind.TutorMessage => ("AI Tutor message", "bx-message-rounded-dots"), _ => ("AI Tutor request", "bx-message-rounded-dots")
            };
            var (outcome, tone) = OutcomeText(e.Kind, e.Outcome);
            return new Row(e.OccurredAt, label, icon, e.Kind, e.QuestionIndex, outcome, tone, After(e.OccurredAt),
                           (e.Flags & (byte)ActivityFlags.OrderUncertain) != 0 || (browser && Near(e.OccurredAt)), browser);
        }).ToList();

        // Opening the tutor after the hand-in: one chat session per mode, created on the first opening.
        foreach (var chat in attempt.ChatSessions)
            rows.Add(new Row(chat.CreatedAt, "AI Tutor opened", "bx-message-rounded", ActivityKind.TutorOpened, null,
                             chat.Mode == "hint" ? "Opened (Get a hint)" : "Opened (Explain)", "secondary", After(chat.CreatedAt), false, false));
        if (submittedAt is { } at)
            rows.Add(new Row(at, $"Work submitted (attempt {attempt.AttemptNumber})", "bx-file", 0, null, "Received", "success", false, false, false, true));
        rows = rows.OrderBy(r => r.At).ThenBy(r => r.IsSubmission ? 1 : 0).ToList();

        var questions = rows.Where(r => r.Question is not null).Select(r => r.Question!.Value).Distinct().OrderBy(q => q).ToList();
        var filtered = rows.Where(r => Matches(r, action) && (question is null || r.Question == question || r.IsSubmission)).ToList();
        var counted = filtered.Where(r => !r.IsSubmission).ToList();
        var tutor = counted.Where(r => r.Kind is ActivityKind.TutorMessage or ActivityKind.TutorRequest).ToList();

        var pages = Math.Max(1, (filtered.Count + PageSize - 1) / PageSize);
        page = Math.Clamp(page, 1, pages);
        var state = events.Count > 0 || attempt.ChatSessions.Count > 0 ? "events"
                  : summary is not null ? "purged"
                  : attempt.CreatedAt < CollectingSince ? "not-collected"
                  : "none";

        return new Timeline
        {
            State = state, Rows = filtered, Page = page, TotalPages = pages,
            PageRows = filtered.Skip((page - 1) * PageSize).Take(PageSize).ToList(),
            Copies = counted.Count(r => r.Kind is ActivityKind.Copy or ActivityKind.Cut),
            Pastes = counted.Count(r => r.Kind == ActivityKind.Paste),
            PastesBlocked = counted.Count(r => r.Kind == ActivityKind.Paste && r.Outcome == "Blocked"),
            HintsRequested = counted.Count(r => r.Kind == ActivityKind.HintRequested),
            HintsReceived = counted.Count(r => r.Kind == ActivityKind.HintRequested && r.Outcome == "Hint given"),
            TutorOpened = counted.Count(r => r.Kind == ActivityKind.TutorOpened),
            TutorRequests = tutor.Count,
            TutorRequestsAfterSubmission = tutor.Count(r => r.AfterSubmission),
            TutorOutcomes = tutor.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count()),
            Summary = summary, Questions = questions, Action = action, Question = question
        };
    }

    private static bool Matches(Row r, string? action) => action switch
    {
        "copy"  => r.Kind is ActivityKind.Copy or ActivityKind.Cut,
        "paste" => r.Kind == ActivityKind.Paste,
        "hint"  => r.Kind == ActivityKind.HintRequested,
        "tutor" => r.Kind is ActivityKind.TutorOpened or ActivityKind.TutorMessage or ActivityKind.TutorRequest,
        _       => true
    };

    public static (string Text, string Tone) OutcomeText(byte kind, byte outcome) => kind == ActivityKind.TutorOpened ? ("Opened", "secondary") : outcome switch
    {
        ActivityOutcome.Allowed      => ("Allowed", "success"),
        ActivityOutcome.Blocked      => ("Blocked", "danger"),
        ActivityOutcome.Received     => (kind == ActivityKind.HintRequested ? "Hint given" : "Answered", "success"),
        ActivityOutcome.LimitReached => ("Limit reached", "warning"),
        ActivityOutcome.Error        => ("Error", "danger"),
        ActivityOutcome.Interrupted  => ("Interrupted", "warning"),
        ActivityOutcome.Refused      => ("Refused", "warning"),
        ActivityOutcome.Pending      => ("No result yet", "secondary"),
        _                            => ("—", "secondary")
    };

    // ── Retention ────────────────────────────────────────────────────────────

    /// <summary>
    /// Removes detailed actions older than the retention period, keeping per-attempt totals. Returns how many were removed.
    /// </summary>
    public async Task<int> PurgeAsync(DateTime? nowUtc = null)
    {
        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-RetentionDays);
        var old = await _db.AttemptActivities.Where(a => a.ReceivedAt < cutoff).ToListAsync();
        if (old.Count == 0) return 0;

        foreach (var g in old.GroupBy(a => (a.AssignmentId, a.StudentId, a.AttemptNumber)))
        {
            var s = await _db.AttemptActivitySummaries.FirstOrDefaultAsync(x =>
                        x.AssignmentId == g.Key.AssignmentId && x.StudentId == g.Key.StudentId && x.AttemptNumber == g.Key.AttemptNumber);
            if (s is null)
            {
                s = new AttemptActivitySummary { AssignmentId = g.Key.AssignmentId, StudentId = g.Key.StudentId, AttemptNumber = g.Key.AttemptNumber };
                _db.AttemptActivitySummaries.Add(s);
            }
            s.Copies         += g.Count(a => a.Kind is ActivityKind.Copy or ActivityKind.Cut);
            s.Pastes         += g.Count(a => a.Kind == ActivityKind.Paste);
            s.PastesBlocked  += g.Count(a => a.Kind == ActivityKind.Paste && a.Outcome == ActivityOutcome.Blocked);
            s.HintsRequested += g.Count(a => a.Kind == ActivityKind.HintRequested);
            s.HintsReceived  += g.Count(a => a.Kind == ActivityKind.HintRequested && a.Outcome == ActivityOutcome.Received);
            s.TutorOpened    += g.Count(a => a.Kind == ActivityKind.TutorOpened);
            s.TutorRequests  += g.Count(a => a.Kind is ActivityKind.TutorMessage or ActivityKind.TutorRequest);
            s.PurgedBefore    = cutoff;
        }
        _db.AttemptActivities.RemoveRange(old);
        await _db.SaveChangesAsync();
        return old.Count;
    }
}

/// <summary>Once a day, removes activity details past the retention period (totals per attempt are kept).</summary>
public class ActivityRetentionSweep : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ActivityRetentionSweep> _logger;

    public ActivityRetentionSweep(IServiceScopeFactory scopes, ILogger<ActivityRetentionSweep> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var removed = await scope.ServiceProvider.GetRequiredService<ActivityLogService>().PurgeAsync();
                if (removed > 0) _logger.LogInformation("Activity retention: removed {Count} detailed action(s)", removed);
            }
            catch (Exception ex) { _logger.LogWarning("Activity retention sweep failed ({Type})", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); } catch (TaskCanceledException) { return; }
        }
    }
}
