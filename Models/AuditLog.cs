namespace SmartSchoolAI.Models;

/// <summary>
/// Immutable audit trail for GDPR compliance.
/// Records every CREATE/UPDATE/DELETE/PUBLISH action with before/after JSON snapshots.
/// </summary>
public class AuditLog
{
    public Guid    Id         { get; set; } = Guid.NewGuid();
    public string  Action     { get; set; } = string.Empty;    // CREATE | UPDATE | DELETE | PUBLISH
    public string  EntityType { get; set; } = string.Empty;    // "Assignment" | "Class"
    public Guid    EntityId   { get; set; }
    public string? OldValue   { get; set; }    // JSON snapshot before change
    public string? NewValue   { get; set; }    // JSON snapshot after change
    public string? IpAddress  { get; set; }
    public string? UserAgent  { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // FK
    public Guid UserId { get; set; }

    // Navigation
    public User User { get; set; } = null!;
}
