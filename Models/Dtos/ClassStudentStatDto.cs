namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// Per-student summary shown in a class's student list:
/// how much of the assigned work was handed in, and how well it scored.
/// </summary>
public class ClassStudentStatDto
{
    public int Assigned  { get; set; }   // assignments this student was given in the class
    public int Submitted { get; set; }   // how many they actually submitted

    /// <summary>Percentage of assigned work submitted (0 when nothing was assigned).</summary>
    public int ParticipationRate => Assigned > 0
        ? (int)Math.Round(Submitted * 100.0 / Assigned)
        : 0;

    /// <summary>Average score across submitted work, or -1 when there is none yet.</summary>
    public int AverageScore { get; set; } = -1;
}
