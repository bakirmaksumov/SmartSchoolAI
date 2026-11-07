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
