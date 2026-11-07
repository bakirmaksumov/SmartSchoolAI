namespace SmartSchoolAI.Models;

/// <summary>
/// Represents AI evaluation of a single step within a student's submission.
/// Each StudentResult contains N StepResults (one per problem step).
/// </summary>
public class StepResult : BaseEntity
{
    public int         QuestionIndex { get; set; }            // 0-based; matches StudentAnswer.QuestionIndex
    public int         StepNumber  { get; set; }
    public string      StepText    { get; set; } = string.Empty;
    public ResultStatus Status     { get; set; }
    public string?     Explanation { get; set; }

    // FK
    public Guid StudentResultId { get; set; }

    // Navigation
    public StudentResult StudentResult { get; set; } = null!;
}
