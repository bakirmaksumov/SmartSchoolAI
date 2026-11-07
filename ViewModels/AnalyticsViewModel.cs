using SmartSchoolAI.Models;

namespace SmartSchoolAI.ViewModels;

/// <summary>
/// Everything the analytics page renders. Replaces a bag of loose ViewBag
/// entries so the view is checked at compile time and renaming a field is safe.
/// </summary>
public class AnalyticsViewModel
{
    // ── Filters ──────────────────────────────────────────────────────────────
    public List<Class> Classes         { get; set; } = new();
    public Guid?       SelectedClassId { get; set; }
    public string      Tab             { get; set; } = "practice";

    /// <summary>False when the teacher has no classes, or the class has no submissions yet.</summary>
    public bool HasData { get; set; }

    // ── Summary ──────────────────────────────────────────────────────────────
    public int AverageScore     { get; set; }
    public int MedianScore      { get; set; }
    public int TotalSubmissions { get; set; }
    public int TotalHours       { get; set; }
    public int TotalMinutes     { get; set; }

    // ── Detail ───────────────────────────────────────────────────────────────
    public List<AnalyticsAssignmentRow> Assignments        { get; set; } = new();
    public List<AnalyticsStudentSummary> StrugglingStudents { get; set; } = new();
    public List<AnalyticsStudentRow>     StudentRows        { get; set; } = new();
}

/// <summary>One assignment column: its averages and the labels the chart needs.</summary>
public class AnalyticsAssignmentRow
{
    public Guid     Id           { get; set; }
    public string   Title        { get; set; } = string.Empty;
    public DateTime CreatedAt    { get; set; }
    public int      AverageScore { get; set; }
    public int      Submissions  { get; set; }

    /// <summary>Title shortened for the chart's x-axis.</summary>
    public string ChartLabel => Title.Length > 30 ? Title[..30] + "…" : Title;
}

/// <summary>A student's standing in the class.</summary>
public class AnalyticsStudentSummary
{
    public Guid   StudentId { get; set; }
    public string FullName  { get; set; } = string.Empty;

    /// <summary>Average across submitted work, or -1 when the student has none.</summary>
    public int AverageScore { get; set; } = -1;

    public int Submissions { get; set; }
}

/// <summary>A row of the student × assignment grid.</summary>
public class AnalyticsStudentRow : AnalyticsStudentSummary
{
    public List<AnalyticsScoreCell> Cells { get; set; } = new();
}

/// <summary>One cell of the grid: the score for a student on an assignment.</summary>
public class AnalyticsScoreCell
{
    public Guid  AssignmentId { get; set; }

    /// <summary>Null when the student has not submitted this assignment.</summary>
    public int?  Score        { get; set; }

    public Guid? ResultId     { get; set; }
}
