using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.ViewModels;

/// <summary>The assignments list with the filters above it.</summary>
public class AssignmentListViewModel
{
    public List<Assignment> Assignments    { get; set; } = new();
    public List<Class>      Classes        { get; set; } = new();
    public Guid?            SelectedClassId { get; set; }
    public string?          SelectedStatus  { get; set; }
}

/// <summary>The assignment page: who it went to, who answered, and how each question fared.</summary>
public class AssignmentDetailsViewModel
{
    public Assignment Assignment { get; set; } = null!;

    /// <summary>Solve rate per question, in question order.</summary>
    public List<QuestionStatDto> QuestionStats { get; set; } = new();

    /// <summary>Assigned students who have not submitted yet.</summary>
    public List<User> NotSubmitted { get; set; } = new();

    /// <summary>Class members the assignment has not been given to yet.</summary>
    public List<User> AvailableStudents { get; set; } = new();

    public int TotalAssigned { get; set; }

    /// <summary>False once a student has submitted — questions are frozen from then on.</summary>
    public bool CanEditQuestions { get; set; }
}

/// <summary>The question editor: the assignment plus what the teacher may still change.</summary>
public class AssignmentEditViewModel
{
    public Assignment  Assignment       { get; set; } = null!;
    public List<Class> Classes          { get; set; } = new();
    public bool        CanEditQuestions { get; set; }
}

/// <summary>One student's submission as the teacher reviews and grades it.</summary>
public class SubmissionDetailViewModel
{
    public StudentResult    Result    { get; set; } = null!;
    public List<QuestionDto> Questions { get; set; } = new();
}

/// <summary>One student's own results/history for a single assignment — the
/// "statistics" drill-down from the assignment's student list, as opposed to
/// the class-wide aggregate on the assignment details page.</summary>
public class StudentStatsViewModel
{
    public Assignment        Assignment { get; set; } = null!;
    public User               Student    { get; set; } = null!;
    public AssignmentStudent  Enrollment { get; set; } = null!;

    /// <summary>Every attempt (submitted or in progress), most recent first.</summary>
    public List<StudentResult> Attempts { get; set; } = new();

    /// <summary>Per-question correctness for the latest submitted attempt only —
    /// pooling across attempts would double-count questions on retakes.</summary>
    public List<QuestionStatDto> QuestionStats { get; set; } = new();
}
