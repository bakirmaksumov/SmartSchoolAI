namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// One row of a class's results view: how an assignment went across the class.
/// </summary>
public class ClassAssignmentStatDto
{
    public int Assigned     { get; set; }   // students the assignment was given to
    public int Submitted    { get; set; }   // how many handed it in

    /// <summary>Average score of submitted work, or -1 when nothing is in yet.</summary>
    public int AverageScore { get; set; } = -1;

    /// <summary>Answers the AI could not confirm — the teacher should check these.</summary>
    public int NeedsReview  { get; set; }
}
