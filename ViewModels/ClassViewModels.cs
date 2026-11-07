using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.ViewModels;

/// <summary>
/// The class page and its four tabs. The <see cref="Class"/> entity is carried
/// as-is because the page renders its own data (name, invite code, enrolments);
/// what this type removes is the untyped ViewBag around it.
/// </summary>
public class ClassDetailsViewModel
{
    public Class  Class { get; set; } = null!;

    /// <summary>Selected tab: assignments | results | students | settings.</summary>
    public string Tab   { get; set; } = "assignments";

    /// <summary>Keyed by student id.</summary>
    public Dictionary<Guid, ClassStudentStatDto> StudentStats { get; set; } = new();

    /// <summary>Keyed by assignment id.</summary>
    public Dictionary<Guid, ClassAssignmentStatDto> AssignmentStats { get; set; } = new();

    public ClassStudentStatDto StatsFor(Guid studentId) =>
        StudentStats.TryGetValue(studentId, out var stat) ? stat : new ClassStudentStatDto();

    public ClassAssignmentStatDto AssignmentStatsFor(Guid assignmentId) =>
        AssignmentStats.TryGetValue(assignmentId, out var stat) ? stat : new ClassAssignmentStatDto();
}

/// <summary>The teacher's cross-class student roster, with its filters.</summary>
public class StudentRosterViewModel
{
    public List<StudentRosterRowDto> Rows            { get; set; } = new();
    public List<Class>               Classes         { get; set; } = new();
    public Guid?                     SelectedClassId { get; set; }
    public string?                   Search          { get; set; }
}
