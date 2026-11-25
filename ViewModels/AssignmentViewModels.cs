using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.ViewModels;

/// <summary>The assignments list with the filters above it.</summary>
public class AssignmentListViewModel
{
    public List<Assignment> Assignments    { get; set; } = new();
    public PageInfo         Page           { get; set; } = new();
    public List<Class>      Classes        { get; set; } = new();
    public Guid?            SelectedClassId { get; set; }
    public string?          SelectedStatus  { get; set; }
}

/// <summary>The assignment page: who it went to, who handed in, and how each question fared.</summary>
public class AssignmentDetailsViewModel
{
    public Assignment          Assignment { get; set; } = null!;

    /// <summary>Rows and counters, by the one set of rules (see <see cref="SmartSchoolAI.Services.AssignmentProgress"/>).</summary>
    public SmartSchoolAI.Services.AssignmentProgress Progress { get; set; } = null!;

    /// <summary>Per question: the newest submitted attempt of each current recipient (unit: students).</summary>
    public List<QuestionStatDto> QuestionStats { get; set; } = new();

    /// <summary>students | questions | details.</summary>
    public string Tab    { get; set; } = "students";

    /// <summary>all | submitted | not-submitted | needs-review.</summary>
    public string Filter { get; set; } = "all";

    /// <summary>False once a student has submitted — questions are frozen from then on.</summary>
    public bool CanEditQuestions { get; set; }

    /// <summary>Requests from late joiners still waiting for a decision.</summary>
    public int PendingRequests { get; set; }

    public AssignPanelViewModel? AssignPanel { get; set; }

    public int QuestionCount { get; set; }
    public decimal TotalMarks { get; set; }
}

/// <summary>The question editor: the assignment plus what the teacher may still change.</summary>
public class AssignmentEditViewModel
{
    public Assignment  Assignment       { get; set; } = null!;
    public List<Class> Classes          { get; set; } = new();
    public bool        CanEditQuestions { get; set; }
    public AssignPanelViewModel AssignPanel { get; set; } = null!;
}

/// <summary>One student's submission as the teacher reviews and grades it.</summary>
public class SubmissionDetailViewModel
{
    public StudentResult    Result    { get; set; } = null!;
    public List<QuestionDto> Questions { get; set; } = new();

    /// <summary>Per question index: what the AI calls behind the answer cost.</summary>
    public Dictionary<int, SmartSchoolAI.Services.AiCostSummary> AiCosts { get; set; } = new();
}

/// <summary>One student's own results/history for a single assignment — the
/// "statistics" drill-down from the assignment's student list, as opposed to
/// the class-wide aggregate on the assignment details page.</summary>
public class StudentStatsViewModel
{
    public Assignment        Assignment { get; set; } = null!;
    public User               Student    { get; set; } = null!;
    public AssignmentStudent  Enrollment { get; set; } = null!;

    /// <summary>Every attempt (submitted or in progress), most recent first.</summary>
    public List<StudentResult> Attempts { get; set; } = new();

    /// <summary>Per-question correctness for the latest submitted attempt only —
    /// pooling across attempts would double-count questions on retakes.</summary>
    public List<QuestionStatDto> QuestionStats { get; set; } = new();
}

/// <summary>
/// The one "who gets this" panel, shown in the editor (before the first assignment) and on the
/// assignment page (to add students). Both post to <c>Assignments/Assign</c>.
/// </summary>
public class AssignPanelViewModel
{
    public Assignment         Assignment    { get; set; } = null!;

    /// <summary>Everyone currently in the assignment's class, by name.</summary>
    public List<User>         Members       { get; set; } = new();

    /// <summary>Students who already have the assignment.</summary>
    public HashSet<Guid>      AssignedIds   { get; set; } = new();

    // What the form starts with: the saved choice, a sensible suggestion, or the teacher's last attempt.
    public AssignmentAudience Audience      { get; set; }
    public HashSet<Guid>      SelectedIds   { get; set; } = new();
    public LateJoinPolicy     LateJoin      { get; set; }
    public DateTime?          AvailableFrom { get; set; }
    public DateTime?          Deadline      { get; set; }

    /// <summary>"edit" or "details": where the teacher comes back to on an error.</summary>
    public string             ReturnTo      { get; set; } = "edit";

    /// <summary>Why the last attempt failed; the form is shown again with the teacher's choices.</summary>
    public string?            Error         { get; set; }

    public bool IsDraft => Assignment.Status == AssignmentStatus.Draft;
}

/// <summary>The teacher's last assign attempt, kept across the redirect when it failed.</summary>
public class AssignAttemptDto
{
    public AssignmentAudience Audience      { get; set; }
    public List<Guid>         StudentIds    { get; set; } = new();
    public LateJoinPolicy     LateJoin      { get; set; }
    public DateTime?          AvailableFrom { get; set; }
    public DateTime?          Deadline      { get; set; }
}

/// <summary>One recipient of an assignment with every attempt they made.</summary>
public class RecipientRow
{
    public User                Student     { get; set; } = null!;
    public AssignmentStudent   Enrollment  { get; set; } = null!;

    /// <summary>All attempts, newest first, including one still in progress.</summary>
    public List<StudentResult> Attempts    { get; set; } = new();

    public List<StudentResult> Submitted   => Attempts.Where(r => r.IsSubmitted).ToList();
    public StudentResult?      Latest      => Attempts.FirstOrDefault(r => r.IsSubmitted);
    public bool                HasSubmitted => Attempts.Any(r => r.IsSubmitted);
    public bool                InProgress  => Attempts.Any(r => !r.IsSubmitted);
    public bool                NeedsReview { get; set; }
}

/// <summary>Submissions waiting for the teacher, across every assignment they can see.</summary>
public class ReviewQueueViewModel
{
    public List<StudentResult> Results { get; set; } = new();
}

/// <summary>One student's work across the teacher's classes.</summary>
public class StudentWorkViewModel
{
    public User                  Student   { get; set; } = null!;
    public List<StudentWorkRow>  Rows      { get; set; } = new();
    public List<Class>           Classes   { get; set; } = new();
    public Guid?                 ClassId   { get; set; }

    /// <summary>Local URL of the page the teacher came from (class tab or roster with its filter).</summary>
    public string                ReturnUrl { get; set; } = "/Classes/Students";
}

public class StudentWorkRow
{
    public Assignment      Assignment  { get; set; } = null!;
    public DateTime?       Due         { get; set; }
    public bool            PersonalDue { get; set; }

    /// <summary>Assigned | In progress | Submitted | Needs review | Reviewed | Overdue | Awaiting approval.</summary>
    public string          Status      { get; set; } = "Assigned";
    public StudentResult?  Latest      { get; set; }
    public bool            NeedsReview { get; set; }
}
