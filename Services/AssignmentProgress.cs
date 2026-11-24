using System.Security.Cryptography;
using System.Text;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>Where one recipient stands. "Submitted" means at least one saved final attempt — nothing else.</summary>
public enum SubmissionState { NotStarted, InProgress, Submitted }

/// <summary>One student and all their attempts at one assignment.</summary>
public sealed class ProgressRow
{
    public User                Student     { get; init; } = null!;

    /// <summary>The recipient record (dates, personal due date); null for a former recipient.</summary>
    public AssignmentStudent?  Enrollment  { get; init; }

    /// <summary>Every attempt, newest first: submitted ones and at most one still in progress.</summary>
    public List<StudentResult> Attempts    { get; init; } = new();

    public List<StudentResult> Submitted   => Attempts.Where(r => r.IsSubmitted).ToList();

    /// <summary>The newest submitted attempt — the one the score, review and main action refer to.</summary>
    public StudentResult?      Latest      => Attempts.FirstOrDefault(r => r.IsSubmitted);

    /// <summary>An attempt the student has started but not submitted (shown in the history, never as handed in).</summary>
    public StudentResult?      Draft       => Attempts.FirstOrDefault(r => !r.IsSubmitted);

    /// <summary>Submitted beats in progress: starting a second attempt does not undo the first hand-in.</summary>
    public SubmissionState     State       => Latest is not null ? SubmissionState.Submitted
                                            : Draft is not null  ? SubmissionState.InProgress
                                            :                      SubmissionState.NotStarted;

    /// <summary>Attempts that need the teacher (the review queue's rule, <see cref="ReviewRules"/>).</summary>
    public List<StudentResult> NeedingReview => Attempts.Where(ReviewRules.Needs).ToList();
    public bool                NeedsReview   => Attempts.Any(ReviewRules.Needs);

    /// <summary>The AI is still marking some answers of a submitted attempt.</summary>
    public bool                MarkingPending => Attempts.Any(r => r.IsSubmitted && r.GradingPending);

    public bool                Reviewed    => Latest?.TeacherReviewedAt is not null;
    public DateTime?           Due         { get; init; }
    public bool                PersonalDue { get; init; }

    /// <summary>Changes whenever anything shown in this student's row changes (used by the live board).</summary>
    public string Version
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(State).Append('|').Append(Due?.Ticks).Append('|').Append(Enrollment?.StartDateOverride?.Ticks);
            foreach (var r in Attempts)
                sb.Append('|').Append(r.Id).Append(':').Append(r.IsSubmitted).Append(':').Append(r.Score)
                  .Append(':').Append(r.TeacherReviewedAt?.Ticks).Append(':').Append(r.GradingPending)
                  .Append(':').Append(ReviewRules.Needs(r)).Append(':').Append(r.IsSubmitted ? (r.UpdatedAt ?? r.SubmittedAt).Ticks : 0);
            return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..12];
        }
    }
}

/// <summary>
/// The one place that decides who counts as submitted, for the assignment page's rows and counters, the live board
/// and the CSV export alike:
///   Assigned      = distinct current recipients;
///   Submitted     = recipients with at least one saved final attempt (a 0% score, work awaiting review or a
///                   teacher's own mark do not change that);
///   Not submitted = all other recipients, either not started or with an attempt in progress;
///   Need review   = recipients with an attempt in the review queue (counted in students; their attempts are in the row).
/// Work by students who are no longer recipients stays visible as "former recipients" and is not counted.
/// </summary>
public sealed class AssignmentProgress
{
    public List<ProgressRow> Rows    { get; private init; } = new();
    public List<ProgressRow> Former  { get; private init; } = new();

    public int Assigned             => Rows.Count;
    public int SubmittedStudents    => Rows.Count(r => r.State == SubmissionState.Submitted);
    public int NotSubmitted         => Assigned - SubmittedStudents;
    public int InProgress           => Rows.Count(r => r.State == SubmissionState.InProgress);
    public int NotStarted           => Rows.Count(r => r.State == SubmissionState.NotStarted);
    public int SubmittedAttempts    => Rows.Sum(r => r.Submitted.Count);
    public int NeedsReviewStudents  => Rows.Count(r => r.NeedsReview);
    public int NeedsReviewAttempts  => Rows.Sum(r => r.NeedingReview.Count);

    /// <summary>Same string on the page and from the polling endpoint, so the board only refreshes on a real change.</summary>
    public string Signature => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(
        string.Join(';', Rows.Concat(Former).OrderBy(r => r.Student.Id).Select(r => $"{r.Student.Id}:{r.Version}")))))[..16];

    /// <summary>
    /// Builds the progress from an assignment loaded with <c>AssignmentStudents.Student</c> and
    /// <c>StudentResults.Student</c> / <c>StudentResults.StudentAnswers</c>.
    /// </summary>
    public static AssignmentProgress Build(Assignment assignment)
    {
        var byStudent = assignment.StudentResults
            .GroupBy(r => r.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.AttemptNumber).ToList());
        var recipients = assignment.AssignmentStudents.Select(x => x.StudentId).ToHashSet();

        var rows = assignment.AssignmentStudents
            .OrderBy(x => x.Student.FullName).ThenBy(x => x.Student.Email)
            .Select(x => new ProgressRow
            {
                Student     = x.Student,
                Enrollment  = x,
                Attempts    = byStudent.TryGetValue(x.StudentId, out var list) ? list : new(),
                Due         = x.DueDateOverride ?? assignment.Deadline,
                PersonalDue = x.DueDateOverride.HasValue
            })
            .ToList();

        var former = byStudent
            .Where(kv => !recipients.Contains(kv.Key) && kv.Value.Count > 0)
            .Select(kv => new ProgressRow { Student = kv.Value[0].Student, Attempts = kv.Value, Due = assignment.Deadline })
            .OrderBy(r => r.Student.FullName)
            .ToList();

        return new AssignmentProgress { Rows = rows, Former = former };
    }
}
