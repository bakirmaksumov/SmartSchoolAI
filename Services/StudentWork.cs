using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>Where a piece of the student's work stands. One rule for the home page and the class page.</summary>
public enum StudentWorkState
{
    /// <summary>Opens later (the student's own start date when set).</summary>
    NotOpenYet,
    /// <summary>Can be started; no attempt yet (or a new attempt can be started after an earlier one).</summary>
    ToStart,
    /// <summary>An attempt the student can still edit.</summary>
    InProgress,
    /// <summary>Handed in and saved; the result is not available yet (being marked or awaiting the teacher).</summary>
    Submitted,
    /// <summary>Handed in and the result is available.</summary>
    Feedback,
    /// <summary>The due date has passed, late work is not accepted and nothing was handed in (or the work is closed).</summary>
    Missed
}

/// <summary>A link the student can follow from a piece of work.</summary>
public sealed record StudentWorkAction(string Label, string Url, string Icon);

/// <summary>One assignment given to the student: its state, dates and the actions that go with it.</summary>
public sealed class StudentWorkItem
{
    public Assignment Assignment { get; init; } = null!;
    public string ClassName { get; init; } = "";
    public Guid ClassId { get; init; }
    /// <summary>The student's due date: their personal one when the teacher set it, otherwise the assignment's.</summary>
    public DateTime? Due { get; init; }
    public bool PersonalDue { get; init; }
    public DateTime? Opens { get; init; }
    public StudentWorkState State { get; init; }
    /// <summary>For <see cref="StudentWorkState.Submitted"/>: "marking" (the AI is still at it) or "review" (waiting for the teacher).</summary>
    public string? Waiting { get; init; }
    public StudentResult? Draft { get; init; }
    public StudentResult? LatestSubmitted { get; init; }
    /// <summary>The newest handed-in attempt whose result the student may see.</summary>
    public StudentResult? LatestFeedback { get; init; }
    public int SubmittedCount { get; init; }
    /// <summary>Past the due date with nothing handed in (late work may still be allowed).</summary>
    public bool Overdue { get; init; }
    public StudentWorkAction? Primary { get; init; }
    public StudentWorkAction? Secondary { get; init; }

    /// <summary>The section of "My work" it belongs to.</summary>
    public string Section => State switch
    {
        StudentWorkState.Submitted => "submitted",
        StudentWorkState.Feedback  => "feedback",
        _ => "todo"
    };

    /// <summary>The student can work on it now (start or continue).</summary>
    public bool Actionable => State is StudentWorkState.ToStart or StudentWorkState.InProgress;

    public string StatusText => State switch
    {
        StudentWorkState.NotOpenYet => $"Opens {SchoolTime.Show(Opens, "dd MMM · HH:mm")}",
        StudentWorkState.ToStart    => SubmittedCount > 0 ? "Another attempt available" : Overdue ? "Overdue" : "Not started",
        StudentWorkState.InProgress => Overdue ? "In progress · overdue" : "In progress",
        StudentWorkState.Submitted  => Waiting == "marking" ? "Submitted · being marked" : "Submitted · awaiting review",
        StudentWorkState.Feedback   => "Feedback ready",
        _                           => Assignment.Status == AssignmentStatus.Active ? "Deadline passed" : "Closed"
    };
}

/// <summary>
/// The one rule for what a student sees for each piece of work, used by "My work" and the class page.
///   * opens later (StartDateOverride, else AvailableFrom)            → Opens …
///   * an attempt not yet handed in, and the work is open              → In progress · Continue (that same attempt)
///   * handed in, result not available (AI marking, or an answer
///     waiting for the teacher — the review queue's rule)              → Submitted · View submission
///   * handed in and the result is available                           → Feedback ready · View feedback
///   * nothing handed in and past the due date, late work not allowed  → Deadline passed
///   * otherwise                                                       → Start assignment
/// A started second attempt keeps the first one reachable: Continue attempt 2 · View submitted attempt 1.
/// The due date is the student's own (DueDateOverride, else Deadline) everywhere: access, sorting, labels, overdue —
/// the same rule the server applies when the work is opened or handed in (<see cref="GradingService.IsPastDeadline"/>).
/// </summary>
public static class StudentWork
{
    /// <summary>The result is visible to the student: handed in, marked, and nothing waits for the teacher.</summary>
    public static bool FeedbackReady(StudentResult r)
        => r.IsSubmitted && !r.GradingPending && !r.StudentAnswers.Any(a => a.NeedsReview || GradingService.IsUnfinished(a));

    public static StudentWorkItem Build(Assignment a, AssignmentStudent enrollment, IReadOnlyCollection<StudentResult> attempts, DateTime now)
    {
        var due       = enrollment.DueDateOverride ?? a.Deadline;
        var opens     = enrollment.StartDateOverride ?? a.AvailableFrom;
        var submitted = attempts.Where(r => r.IsSubmitted).OrderByDescending(r => r.AttemptNumber).ToList();
        var draft     = attempts.Where(r => !r.IsSubmitted).OrderByDescending(r => r.AttemptNumber).FirstOrDefault();
        var latest    = submitted.FirstOrDefault();
        var feedback  = submitted.FirstOrDefault(FeedbackReady);
        var blocked   = GradingService.IsPastDeadline(a, enrollment) || a.Status != AssignmentStatus.Active;
        var attemptsLeft = a.AllowsUnlimitedAttempts || submitted.Count < a.MaxAttempts;
        var overdue   = latest is null && due is { } d && d < now;

        string Take() => $"/Student/TakeAssignment?assignmentId={a.Id}";
        string Result(StudentResult r) => $"/Student/AssignmentResult?assignmentId={a.Id}&resultId={r.Id}";

        StudentWorkState state;
        string? waiting = null;
        StudentWorkAction? primary = null, secondary = null;

        if (latest is null && draft is null && opens is { } o && o > now && a.Status == AssignmentStatus.Active)
        {
            state = StudentWorkState.NotOpenYet;
        }
        else if (draft is not null && !blocked && attemptsLeft)
        {
            state = StudentWorkState.InProgress;
            var n = submitted.Count + 1;
            primary = new(submitted.Count > 0 ? $"Continue attempt {n}" : "Continue assignment", Take(), "bx-edit");
            if (latest is not null) secondary = new($"View submitted attempt {latest.AttemptNumber}", Result(latest), "bx-show");
        }
        else if (latest is not null)
        {
            if (feedback is not null && feedback.Id == latest.Id)
            {
                state = StudentWorkState.Feedback;
                primary = new("View feedback", Result(latest), "bx-message-square-check");
            }
            else
            {
                state = StudentWorkState.Submitted;
                waiting = latest.GradingPending ? "marking" : "review";
                primary = new("View submission", Result(latest), "bx-file");
                if (feedback is not null) secondary = new($"Feedback for attempt {feedback.AttemptNumber}", Result(feedback), "bx-message-square-check");
            }
            if (secondary is null && !blocked && attemptsLeft)
                secondary = new($"Start attempt {submitted.Count + 1}", Take(), "bx-refresh");
        }
        else if (blocked)
        {
            state = StudentWorkState.Missed;
        }
        else
        {
            state = StudentWorkState.ToStart;
            primary = new("Start assignment", Take(), "bx-play");
        }

        return new StudentWorkItem
        {
            Assignment = a, ClassName = a.Class?.Name ?? "", ClassId = a.ClassId,
            Due = due, PersonalDue = enrollment.DueDateOverride.HasValue, Opens = opens,
            State = state, Waiting = waiting, Draft = draft, LatestSubmitted = latest, LatestFeedback = feedback,
            SubmittedCount = submitted.Count, Overdue = overdue, Primary = primary, Secondary = secondary
        };
    }

    /// <summary>
    /// All work given to the student in classes they are still in (active and closed assignments; drafts never
    /// shown to students), in "My work" order. Three queries, whatever the number of assignments.
    /// </summary>
    public static async Task<List<StudentWorkItem>> LoadAsync(AppDbContext db, Guid studentId, DateTime now, Guid? classId = null)
    {
        var classIds = await db.ClassStudents
            .Where(cs => cs.StudentId == studentId && !cs.Class.IsArchived && (classId == null || cs.ClassId == classId))
            .Select(cs => cs.ClassId).ToListAsync();

        var given = await db.AssignmentStudents
            .Include(x => x.Assignment).ThenInclude(a => a.Class)
            .Where(x => x.StudentId == studentId && classIds.Contains(x.Assignment.ClassId)
                     && (x.Assignment.Status == AssignmentStatus.Active || x.Assignment.Status == AssignmentStatus.Closed))
            .ToListAsync();

        var ids = given.Select(x => x.AssignmentId).ToList();
        var attempts = await db.StudentResults
            .Include(r => r.StudentAnswers)
            .Where(r => r.StudentId == studentId && ids.Contains(r.AssignmentId))
            .ToListAsync();
        var byAssignment = attempts.GroupBy(r => r.AssignmentId).ToDictionary(g => g.Key, g => (IReadOnlyCollection<StudentResult>)g.ToList());

        return Order(given
            .Select(x => Build(x.Assignment, x, byAssignment.TryGetValue(x.AssignmentId, out var list) ? list : Array.Empty<StudentResult>(), now))
            // A closed assignment the student never handed in has nothing left to do or see.
            .Where(i => !(i.Assignment.Status == AssignmentStatus.Closed && i.LatestSubmitted is null)))
            .ToList();
    }

    /// <summary>To do first (in progress, then by the student's due date), then submitted and feedback, newest first.</summary>
    public static IEnumerable<StudentWorkItem> Order(IEnumerable<StudentWorkItem> items) => items
        .OrderBy(i => i.State switch
        {
            StudentWorkState.InProgress => 0, StudentWorkState.ToStart => 1, StudentWorkState.NotOpenYet => 2,
            StudentWorkState.Missed => 3, StudentWorkState.Submitted => 4, _ => 5
        })
        .ThenBy(i => i.Actionable || i.State == StudentWorkState.NotOpenYet ? (i.State == StudentWorkState.NotOpenYet ? i.Opens : i.Due) ?? DateTime.MaxValue : DateTime.MaxValue)
        .ThenByDescending(i => i.LatestSubmitted?.SubmittedAt ?? DateTime.MinValue)
        .ThenBy(i => i.Assignment.Title);

    /// <summary>"Due today, 18:00", "Due tomorrow, 09:00", "Due 27 Nov, 18:00" in school time; "No due date".</summary>
    public static string DueText(DateTime? dueUtc, DateTime nowUtc)
    {
        if (dueUtc is not { } due) return "No due date";
        var d = SchoolTime.ToSchool(due);
        var today = SchoolTime.ToSchool(nowUtc).Date;
        var day = d.Date == today ? "today" : d.Date == today.AddDays(1) ? "tomorrow" : d.Date == today.AddDays(-1) ? "yesterday"
                : d.ToString(d.Year == today.Year ? "dd MMM" : "dd MMM yyyy");
        return $"Due {day}, {d:HH:mm}";
    }
}
