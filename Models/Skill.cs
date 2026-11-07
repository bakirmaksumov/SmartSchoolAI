namespace SmartSchoolAI.Models;

/// <summary>
/// Hierarchical skill tree: Subject (0) → Grade (1) → Topic (2) → Skill (3).
/// Self-referencing via ParentId for tree traversal.
/// Code format example: MATH.8.ALG.LINEAR
/// </summary>
public class Skill
{
    public Guid    Id         { get; set; } = Guid.NewGuid();
    public string  Name       { get; set; } = string.Empty;
    public string? Code       { get; set; }
    public int     Level      { get; set; }    // 0=Subject, 1=Grade, 2=Topic, 3=Skill
    public string? Subject    { get; set; }
    public int?    GradeLevel { get; set; }
    public bool    IsActive   { get; set; } = true;

    // FK self-reference
    public Guid? ParentId { get; set; }

    // Navigation
    public Skill?             Parent   { get; set; }
    public ICollection<Skill> Children { get; set; } = new List<Skill>();
}
