namespace SmartSchoolAI.Models;

/// <summary>
/// Represents a system role (Admin, Teacher, Student).
/// Permissions stored as JSON for flexible access control.
/// </summary>
public class Role
{
    public Guid   Id          { get; set; } = Guid.NewGuid();
    public string Name        { get; set; } = string.Empty;
    public string Permissions { get; set; } = "{}";   // JSON permissions map

    // Navigation
    public ICollection<User> Users { get; set; } = new List<User>();

    // Role name constants — avoids magic strings throughout the codebase
    public const string Admin   = "Admin";
    public const string Teacher = "Teacher";
    public const string Student = "Student";
}
