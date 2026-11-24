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

    /// <summary>"2", "2a" — as students see it.</summary>
    public string Label     { get; set; } = string.Empty;

    /// <summary>Shared text above parts: shown as a heading, nothing to answer.</summary>
    public bool   IsStem    { get; set; }

    /// <summary>A part ("2a") of a question with shared text.</summary>
    public bool   IsPart    { get; set; }

    /// <summary>Answers given but not marked yet (AI still checking or could not mark).</summary>
    public int    NotMarked { get; set; }

    /// <summary>Students counted: current recipients with a submitted attempt.</summary>
    public int    Students  { get; set; }
}
