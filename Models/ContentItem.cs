namespace SmartSchoolAI.Models;

/// <summary>
/// A reusable content item in the teacher's library (problems, explanations, etc.).
/// Content field stores a JSON structure for flexible content types.
/// </summary>
public class ContentItem : BaseEntity
{
    public string  Title      { get; set; } = string.Empty;
    public string? Subject    { get; set; }
    public int?    GradeLevel { get; set; }
    public string  Content    { get; set; } = "{}";
    public int     Difficulty { get; set; } = 1;    // 1-5
    public bool    IsApproved { get; set; } = false;

    // FK
    public Guid CreatedById { get; set; }

    // Navigation
    public User CreatedBy { get; set; } = null!;
}
