namespace SmartSchoolAI.Models;

/// <summary>
/// Represents a teacher's class/group of students for a specific subject.
/// Each class has a unique invite code students use to enroll.
/// </summary>
public class Class : BaseEntity
{
    public string   Name             { get; set; } = string.Empty;
    public int      GradeLevel       { get; set; }
    public string   Subject          { get; set; } = string.Empty;
    public string   AcademicYear     { get; set; } = string.Empty;
    public string   InviteCode       { get; set; } = string.Empty;   // 6 chars, UNIQUE
    public DateTime? InviteExpiresAt { get; set; }                   // null = never expires
    public int      MaxStudents      { get; set; } = 50;
    public bool     IsArchived       { get; set; } = false;

    // ── Multilingual and accessibility support ──────────────────────────────
    /// <summary>Lets students translate questions and feedback into another language.</summary>
    public bool   TranslationEnabled { get; set; } = false;
    /// <summary>Lets students have questions and feedback read aloud.</summary>
    public bool   VoiceEnabled       { get; set; } = false;
    /// <summary>JSON array of language codes students may translate into (e.g. ["es","fr"]).</summary>
    public string EnabledLanguages   { get; set; } = "[]";

    // FK
    public Guid TeacherId { get; set; }

    // Navigation
    public User                       Teacher        { get; set; } = null!;
    public ICollection<Assignment>    Assignments    { get; set; } = new List<Assignment>();
    public ICollection<ClassStudent>  ClassStudents  { get; set; } = new List<ClassStudent>();
    public ICollection<ClassTeacher>  CoTeachers     { get; set; } = new List<ClassTeacher>();

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hides the class from active teacher lists and from its students.
    /// Nothing is deleted — assignments, submissions and enrolments are kept.
    /// </summary>
    public void Archive()
    {
        IsArchived = true;
        UpdatedAt  = DateTime.UtcNow;
    }

    /// <summary>Brings an archived class back into active use.</summary>
    public void Restore()
    {
        IsArchived = false;
        UpdatedAt  = DateTime.UtcNow;
    }
}
