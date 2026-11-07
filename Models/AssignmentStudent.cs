namespace SmartSchoolAI.Models;

/// <summary>
/// Join entity for per-assignment student assignment.
/// A teacher explicitly assigns students to each assignment.
/// Composite PK: (AssignmentId, StudentId).
/// </summary>
public class AssignmentStudent
{
    public Guid     AssignmentId { get; set; }
    public Guid     StudentId    { get; set; }
    public DateTime AssignedAt   { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Per-student override of when this assignment becomes available /
    /// is due. Null means "use the assignment's own AvailableFrom/Deadline" —
    /// most students have no override, only the ones a teacher gave a custom
    /// schedule to (e.g. an extension, or a staggered start).
    /// </summary>
    public DateTime? StartDateOverride { get; set; }
    public DateTime? DueDateOverride   { get; set; }

    // Navigation
    public Assignment Assignment { get; set; } = null!;
    public User       Student    { get; set; } = null!;
}
