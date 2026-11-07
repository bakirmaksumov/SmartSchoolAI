namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// One row of the teacher's cross-class student roster.
/// A student enrolled in two classes appears once per class.
/// </summary>
public class StudentRosterRowDto
{
    public Guid     StudentId { get; set; }
    public string   FullName  { get; set; } = string.Empty;
    public string   Email     { get; set; } = string.Empty;
    public DateTime JoinedAt  { get; set; }

    public Guid   ClassId   { get; set; }
    public string ClassName { get; set; } = string.Empty;

    public ClassStudentStatDto Stats { get; set; } = new();
}
