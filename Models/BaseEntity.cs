namespace SmartSchoolAI.Models;

/// <summary>
/// Base class for all domain entities.
/// Provides common audit fields and soft-delete support.
/// </summary>
public abstract class BaseEntity
{
    public Guid      Id        { get; set; } = Guid.NewGuid();
    public DateTime  CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }   // NULL = not deleted (soft delete)

    public bool IsDeleted => DeletedAt.HasValue;
}
