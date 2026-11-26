namespace SmartSchoolAI.Models;

public enum LateJoinRequestStatus
{
    /// <summary>Waiting for the teacher ("decide later" keeps it here).</summary>
    Pending  = 0,
    /// <summary>The student was given the assignment.</summary>
    Approved = 1,
    /// <summary>Closed without giving the assignment.</summary>
    Declined = 2
}

/// <summary>
/// A student joined a class after an assignment with <see cref="LateJoinPolicy.TeacherApproval"/> was given
/// to the whole class. Until a teacher approves it the student has no access to the assignment: access only
/// ever comes from an <see cref="AssignmentStudent"/> row, which approving creates.
/// One request per (assignment, student) — a unique index makes a second one impossible.
/// </summary>
public class LateJoinRequest
{
    public Guid                  Id           { get; set; } = Guid.NewGuid();
    public Guid                  AssignmentId { get; set; }
    public Guid                  StudentId    { get; set; }
    public LateJoinRequestStatus Status       { get; set; } = LateJoinRequestStatus.Pending;
    public DateTime              RequestedAt  { get; set; } = DateTime.UtcNow;
    public DateTime?             DecidedAt    { get; set; }
    public Guid?                 DecidedById  { get; set; }

    // Navigation
    public Assignment Assignment { get; set; } = null!;
    public User       Student    { get; set; } = null!;
}
