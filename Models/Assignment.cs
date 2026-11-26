namespace SmartSchoolAI.Models;

public enum AssignmentType
{
    ClassroomPractice = 0,
    Homework          = 1
}

public enum AssignmentStatus
{
    Draft    = 0,
    Active   = 1,
    Closed   = 2,
    Archived = 3
}

/// <summary>How the teacher chose who gets an assignment.</summary>
public enum AssignmentAudience
{
    /// <summary>Hand-picked students. Also what every assignment made before this choice existed is.</summary>
    SelectedStudents = 0,
    /// <summary>The whole class at the moment of assigning; newcomers follow <see cref="LateJoinPolicy"/>.</summary>
    EntireClass      = 1
}

/// <summary>What happens to a student who joins the class after an assignment was given out.</summary>
public enum LateJoinPolicy
{
    /// <summary>Nothing: the teacher assigns by hand. Every assignment made before this choice existed keeps this.</summary>
    Manual          = 0,
    /// <summary>The newcomer is given the assignment straight away.</summary>
    AutoAssign      = 1,
    /// <summary>A request waits for the teacher; the newcomer sees nothing until it is approved.</summary>
    TeacherApproval = 2
}

/// <summary>Scale a score is reported in. The stored score is always a percentage.</summary>
public enum GradingType
{
    Percentage = 0,
    Letter     = 1,
    PassFail   = 2,
    Gpa        = 3
}

/// <summary>
/// Core domain entity representing a teacher's assignment.
/// Encapsulates a state machine: Draft → Active → Closed → Archived.
/// Business rules are enforced inside domain methods (Tell, Don't Ask).
/// </summary>
public class Assignment : BaseEntity
{
    public string           Title               { get; set; } = string.Empty;
    public string?          Description         { get; set; }
    public AssignmentType   Type                { get; set; }
    public AssignmentStatus Status              { get; set; } = AssignmentStatus.Draft;
    public DateTime?        Deadline            { get; set; }
    public DateTime?        AvailableFrom       { get; set; }
    public string           SkillIds            { get; set; } = "[]";   // JSON: UUID[]
    public string           Questions           { get; set; } = "[]";   // JSON
    public bool             AllowLateSubmission { get; set; } = false;

    /// <summary>How many graded attempts a student gets. 0 means unlimited.</summary>
    public int              MaxAttempts         { get; set; } = 1;

    /// <summary>Scale used when showing this assignment's score.</summary>
    public GradingType      GradingType         { get; set; } = GradingType.Percentage;

    /// <summary>Score (0–100) a student needs to pass. Subjects differ, so it's per assignment.</summary>
    public int              PassThreshold       { get; set; } = 70;

    /// <summary>
    /// Blocks pasting into answer fields. A soft deterrent against pasting an
    /// answer straight from a chatbot — it can be bypassed, but it discourages it.
    /// </summary>
    public bool             AntiCopyPaste       { get; set; } = false;

    /// <summary>How the recipients were chosen. Set when the teacher assigns the work.</summary>
    public AssignmentAudience Audience       { get; set; } = AssignmentAudience.SelectedStudents;

    /// <summary>Rule for students who join the class later. Only applies to an <see cref="AssignmentAudience.EntireClass"/> assignment.</summary>
    public LateJoinPolicy     LateJoinPolicy { get; set; } = LateJoinPolicy.Manual;

    /// <summary>True when a student who joins the class now is given this work, directly or after approval.</summary>
    public bool FollowsNewClassMembers
        => Audience == AssignmentAudience.EntireClass && LateJoinPolicy != LateJoinPolicy.Manual;

    /// <summary>The rule a teacher is offered before choosing: practice goes out automatically, homework waits for approval.</summary>
    public static LateJoinPolicy SuggestedLateJoinPolicy(AssignmentType type)
        => type == AssignmentType.ClassroomPractice ? LateJoinPolicy.AutoAssign : LateJoinPolicy.TeacherApproval;

    /// <summary>
    /// Whether a student added now could still do this work: it must be Active, and past its deadline
    /// only when late submissions are allowed (or the student gets their own later due date).
    /// </summary>
    public bool AcceptsNewRecipient(DateTime now, DateTime? dueOverride = null)
    {
        if (Status != AssignmentStatus.Active) return false;
        var due = dueOverride ?? Deadline;
        return !(due.HasValue && due.Value < now && !AllowLateSubmission);
    }

    /// <summary>When false, the AI tutor guides but never reveals the final answer.</summary>
    public bool             AllowSolutionReveal { get; set; } = false;

    /// <summary>
    /// Lets students check a single answer while solving and see whether it is
    /// right. Off by default for graded homework so answers can't be brute-forced.
    /// </summary>
    public bool             AllowInstantFeedback { get; set; } = false;

    /// <summary>
    /// Instant per-question feedback is the whole point of classroom practice,
    /// so it is always on there; homework only shows it when the teacher opts in.
    /// </summary>
    public bool ShowsInstantFeedback
        => AllowInstantFeedback || Type == AssignmentType.ClassroomPractice;

    /// <summary>
    /// True when the student may keep retrying. Driven by the teacher's setting
    /// rather than the assignment type, so practice can be capped and homework
    /// can be unlimited when that is what the teacher wants.
    /// </summary>
    public bool AllowsUnlimitedAttempts => MaxAttempts <= 0;

    /// <summary>True when a score reaches this assignment's pass mark.</summary>
    public bool IsPass(int score) => score >= PassThreshold;

    /// <summary>
    /// Renders a percentage score in this assignment's grading scale.
    /// Letter and GPA use standard 90/80/70/60 bands; pass/fail uses the
    /// assignment's own threshold.
    /// </summary>
    public string FormatGrade(int score) => GradingType switch
    {
        GradingType.PassFail => IsPass(score) ? "Pass" : "Fail",
        GradingType.Letter   => score >= 90 ? "A"
                              : score >= 80 ? "B"
                              : score >= 70 ? "C"
                              : score >= 60 ? "D" : "F",
        GradingType.Gpa      => score >= 90 ? "4.0"
                              : score >= 80 ? "3.0"
                              : score >= 70 ? "2.0"
                              : score >= 60 ? "1.0" : "0.0",
        _                    => $"{score}%"
    };

    // FK
    public Guid  ClassId      { get; set; }
    public Guid  TeacherId    { get; set; }
    public Guid? ContentRefId { get; set; }

    // Navigation
    public Class                            Class              { get; set; } = null!;
    public User                             Teacher            { get; set; } = null!;
    public ICollection<StudentResult>       StudentResults     { get; set; } = new List<StudentResult>();
    public ICollection<AssignmentStudent>   AssignmentStudents { get; set; } = new List<AssignmentStudent>();

    // ── State Machine ────────────────────────────────────────────────────────

    /// <summary>Transitions Draft → Active. Validates deadline for Homework type.</summary>
    public void Publish()
    {
        if (Status != AssignmentStatus.Draft)
            throw new InvalidOperationException($"Publish requires Draft status. Current: {Status}");

        if (Type == AssignmentType.Homework && Deadline is null)
            throw new InvalidOperationException("Homework requires a deadline before publishing.");

        if (Deadline.HasValue && Deadline.Value <= DateTime.UtcNow.AddHours(1))
            throw new InvalidOperationException("Deadline must be at least 1 hour in the future.");

        Status    = AssignmentStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Transitions Closed → Active. Closing is a one-click action a teacher can
    /// hit by mistake, so it must be reversible; nothing about the work is lost
    /// while an assignment is closed.
    /// </summary>
    public void Reopen()
    {
        if (Status != AssignmentStatus.Closed)
            throw new InvalidOperationException($"Reopen requires Closed status. Current: {Status}");

        Status    = AssignmentStatus.Active;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Questions may still be edited while no one has answered them — including
    /// just after assigning, so a typo can be fixed. Once a student has submitted,
    /// the set is frozen: answers are stored against question positions, so
    /// adding or removing one would silently mis-align everything already graded.
    /// </summary>
    public bool AllowsQuestionEditing(bool hasSubmissions)
        => (Status is AssignmentStatus.Draft or AssignmentStatus.Active) && !hasSubmissions;

    /// <summary>Transitions Active → Closed.</summary>
    public void Close()
    {
        if (Status != AssignmentStatus.Active)
            throw new InvalidOperationException($"Close requires Active status. Current: {Status}");

        Status    = AssignmentStatus.Closed;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Transitions any status → Archived.</summary>
    public void Archive()
    {
        Status    = AssignmentStatus.Archived;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Extends the deadline. Only valid for Active assignments.</summary>
    public void ExtendDeadline(DateTime newDeadline)
    {
        if (Status != AssignmentStatus.Active)
            throw new InvalidOperationException("Can only extend deadline for Active assignments.");

        if (newDeadline <= DateTime.UtcNow)
            throw new InvalidOperationException("New deadline must be in the future.");

        Deadline  = newDeadline;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>Soft-deletes the assignment. Guards against deleting with submissions.</summary>
    public void SoftDelete(bool hasSubmissions)
    {
        if (hasSubmissions)
            throw new InvalidOperationException("Cannot delete assignment with student submissions.");

        if (Status == AssignmentStatus.Active)
            throw new InvalidOperationException("Close the assignment before deleting.");

        DeletedAt = DateTime.UtcNow;
    }
}
