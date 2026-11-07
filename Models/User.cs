namespace SmartSchoolAI.Models;

/// <summary>
/// Represents a system user (teacher, student, or admin).
/// Email is unique and stored in lowercase.
/// </summary>
public class User : BaseEntity
{
    public string  Email              { get; set; } = string.Empty;
    public string  PasswordHash       { get; set; } = string.Empty;
    public string  FullName           { get; set; } = string.Empty;
    public string? AvatarUrl          { get; set; }
    public bool    IsActive           { get; set; } = true;
    public bool    MustChangePassword { get; set; } = false;
    public DateTime? LastLoginAt      { get; set; }
    public string? School             { get; set; }
    public string? Bio                { get; set; }

    // FK
    public Guid RoleId { get; set; }

    // Navigation
    public Role                  Role           { get; set; } = null!;
    public ICollection<Class>    TeacherClasses { get; set; } = new List<Class>();
    public ICollection<AuditLog> AuditLogs      { get; set; } = new List<AuditLog>();
}
