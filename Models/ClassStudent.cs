namespace SmartSchoolAI.Models;

/// <summary>
/// Join entity for the many-to-many relationship between Class and User (Student).
/// Composite PK: (ClassId, StudentId).
/// </summary>
public class ClassStudent
{
    public Guid     ClassId   { get; set; }
    public Guid     StudentId { get; set; }
    public DateTime JoinedAt  { get; set; } = DateTime.UtcNow;

    // Navigation
    public Class Class   { get; set; } = null!;
    public User  Student { get; set; } = null!;
}
