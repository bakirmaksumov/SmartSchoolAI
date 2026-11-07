namespace SmartSchoolAI.Models;

/// <summary>
/// Join entity granting a second teacher (co-teacher) the same access as the
/// class owner (<see cref="Class.TeacherId"/>). Composite PK: (ClassId, TeacherId).
/// </summary>
public class ClassTeacher
{
    public Guid     ClassId   { get; set; }
    public Guid     TeacherId { get; set; }
    public DateTime AddedAt   { get; set; } = DateTime.UtcNow;

    // Navigation
    public Class Class   { get; set; } = null!;
    public User  Teacher { get; set; } = null!;
}
