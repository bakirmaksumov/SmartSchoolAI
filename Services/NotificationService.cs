using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>One entry of the notifications bell: what happened, when, and where it leads.</summary>
public sealed record NotificationItem(string Kind, string Icon, string Color, string Title, string? Detail, string? Url, DateTime At)
{
    public bool IsNewSince(DateTime? seenAt) => seenAt is null || At > seenAt;
}

/// <summary>
/// The notifications bell, built from what already happened in the school — no separate notification records:
/// <list type="bullet">
/// <item>a teacher sees work handed in on their assignments (flagged when it waits for their review), requests to
/// join an assignment late, and problems students reported in a question;</item>
/// <item>a student sees new assignments, work their teacher has checked, and answers to their late-join requests.</item>
/// </list>
/// Only the last <see cref="Days"/> days are shown. What counts as new is everything after the user last opened the
/// bell (<see cref="User.NotificationsSeenAt"/>).
/// </summary>
public class NotificationService
{
    public const int Days = 30;
    public const int MaxItems = 15;
    private readonly AppDbContext _db;

    public NotificationService(AppDbContext db) => _db = db;

    public sealed record Feed(List<NotificationItem> Items, int Unread, DateTime? SeenAt);

    public async Task<Feed> ForUserAsync(Guid userId, string? role, DateTime? now = null)
    {
        var since = (now ?? DateTime.UtcNow).AddDays(-Days);
        var seenAt = await _db.Users.Where(u => u.Id == userId).Select(u => u.NotificationsSeenAt).FirstOrDefaultAsync();
        var items = role switch
        {
            "Teacher" => await TeacherAsync(userId, since),
            "Student" => await StudentAsync(userId, since),
            _ => new List<NotificationItem>(),
        };
        items = items.OrderByDescending(i => i.At).Take(MaxItems).ToList();
        return new Feed(items, items.Count(i => i.IsNewSince(seenAt)), seenAt);
    }

    /// <summary>Marks everything up to now as seen.</summary>
    public async Task MarkSeenAsync(Guid userId, DateTime? now = null)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return;
        user.NotificationsSeenAt = now ?? DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task<List<NotificationItem>> TeacherAsync(Guid teacherId, DateTime since)
    {
        var mine = _db.Assignments.AccessibleBy(teacherId).Select(a => a.Id);
        var list = new List<NotificationItem>();

        var handedIn = await _db.StudentResults.AsNoTracking()
            .Where(r => r.IsSubmitted && r.SubmittedAt >= since && mine.Contains(r.AssignmentId))
            .OrderByDescending(r => r.SubmittedAt).Take(MaxItems)
            .Select(r => new
            {
                r.Id, r.SubmittedAt, Student = r.Student.FullName, r.Assignment.Title,
                Waiting = r.TeacherReviewedAt == null && (r.GradingPending || r.StudentAnswers.Any(a => a.NeedsReview)),
            }).ToListAsync();
        list.AddRange(handedIn.Select(r => new NotificationItem(
            r.Waiting ? "review" : "submitted",
            r.Waiting ? "bx-error-circle" : "bx-check-circle",
            r.Waiting ? "warning" : "success",
            r.Waiting ? $"{r.Student}'s work needs your review" : $"{r.Student} handed in their work",
            r.Title, $"/Assignments/SubmissionDetail?resultId={r.Id}", r.SubmittedAt)));

        var joins = await _db.LateJoinRequests.AsNoTracking()
            .Where(j => j.Status == LateJoinRequestStatus.Pending && j.RequestedAt >= since && mine.Contains(j.AssignmentId))
            .OrderByDescending(j => j.RequestedAt).Take(MaxItems)
            .Select(j => new { j.AssignmentId, j.RequestedAt, Student = j.Student.FullName, j.Assignment.Title }).ToListAsync();
        list.AddRange(joins.Select(j => new NotificationItem("late-join", "bx-user-plus", "info",
            $"{j.Student} joined late and waits for your approval", j.Title, $"/Assignments/Details/{j.AssignmentId}", j.RequestedAt)));

        var reports = await _db.QuestionReports.AsNoTracking()
            .Where(q => !q.Resolved && q.CreatedAt >= since && mine.Contains(q.AssignmentId))
            .OrderByDescending(q => q.CreatedAt).Take(MaxItems)
            .Select(q => new { q.AssignmentId, q.CreatedAt, q.QuestionIndex, Student = q.Student.FullName, q.Assignment.Title }).ToListAsync();
        list.AddRange(reports.Select(q => new NotificationItem("report", "bx-flag", "danger",
            $"{q.Student} reported a problem in question {q.QuestionIndex + 1}", q.Title, $"/Assignments/Details/{q.AssignmentId}", q.CreatedAt)));

        return list;
    }

    private async Task<List<NotificationItem>> StudentAsync(Guid studentId, DateTime since)
    {
        var list = new List<NotificationItem>();

        var assigned = await _db.AssignmentStudents.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.AssignedAt >= since && x.Assignment.Status == AssignmentStatus.Active)
            .OrderByDescending(x => x.AssignedAt).Take(MaxItems)
            .Select(x => new { x.AssignmentId, x.AssignedAt, x.Assignment.Title, Class = x.Assignment.Class.Name }).ToListAsync();
        list.AddRange(assigned.Select(a => new NotificationItem("assigned", "bx-book-open", "primary",
            "New assignment", $"{a.Title} · {a.Class}", $"/Student/TakeAssignment?assignmentId={a.AssignmentId}", a.AssignedAt)));

        var checkedWork = await _db.StudentResults.AsNoTracking()
            .Where(r => r.StudentId == studentId && r.IsSubmitted && r.TeacherReviewedAt != null && r.TeacherReviewedAt >= since)
            .OrderByDescending(r => r.TeacherReviewedAt).Take(MaxItems)
            .Select(r => new { r.Id, r.AssignmentId, At = r.TeacherReviewedAt!.Value, r.Assignment.Title }).ToListAsync();
        list.AddRange(checkedWork.Select(r => new NotificationItem("checked", "bx-check-double", "success",
            "Your teacher checked your work", r.Title, $"/Student/AssignmentResult?assignmentId={r.AssignmentId}&resultId={r.Id}", r.At)));

        var decided = await _db.LateJoinRequests.AsNoTracking()
            .Where(j => j.StudentId == studentId && j.DecidedAt != null && j.DecidedAt >= since && j.Status != LateJoinRequestStatus.Pending)
            .OrderByDescending(j => j.DecidedAt).Take(MaxItems)
            .Select(j => new { j.AssignmentId, At = j.DecidedAt!.Value, j.Status, j.Assignment.Title }).ToListAsync();
        list.AddRange(decided.Select(j => j.Status == LateJoinRequestStatus.Approved
            ? new NotificationItem("join-approved", "bx-door-open", "success", "You can now start this assignment", j.Title,
                                   $"/Student/TakeAssignment?assignmentId={j.AssignmentId}", j.At)
            : new NotificationItem("join-declined", "bx-block", "secondary", "Your request to join was declined", j.Title, null, j.At)));

        return list;
    }
}
