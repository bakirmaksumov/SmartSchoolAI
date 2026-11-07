namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// Aggregate performance of a single question across all submissions of an
/// assignment — used to surface which questions students struggle with.
/// </summary>
public class QuestionStatDto
{
    public int    Index     { get; set; }             // 0-based question index
    public string Title     { get; set; } = string.Empty;
    public int    Answered  { get; set; }             // how many students answered it
    public int    Correct   { get; set; }             // how many got it right

    /// <summary>Percentage correct among those who answered, or -1 if unanswered.</summary>
    public int    SolveRate { get; set; }
}
