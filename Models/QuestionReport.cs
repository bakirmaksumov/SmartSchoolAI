namespace SmartSchoolAI.Models;

/// <summary>
/// A student's "Report a problem" note on one question of an assignment —
/// e.g. a wrong answer key, a typo, or a diagram that didn't load. Read by
/// teachers; never auto-actioned.
/// </summary>
public class QuestionReport : BaseEntity
{
    public int    QuestionIndex { get; set; }   // 0-based; matches StudentAnswer.QuestionIndex
    public string Message       { get; set; } = string.Empty;
    public bool   Resolved      { get; set; }

    // FK
    public Guid AssignmentId { get; set; }
    public Guid StudentId    { get; set; }

    // Navigation
    public Assignment Assignment { get; set; } = null!;
    public User        Student    { get; set; } = null!;
}
