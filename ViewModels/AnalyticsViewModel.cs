using Microsoft.AspNetCore.Http;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.ViewModels;

/// <summary>
/// The teacher's analytics page for one class: Overview, Assignments, Students, Questions and the "work" list the
/// counters open. Everything is counted in <see cref="AnalyticsWork"/> items — one assignment given to one student —
/// built by <see cref="AnalyticsService"/>.
/// </summary>
public class AnalyticsViewModel
{
    // ── Filters (all kept in the address) ────────────────────────────────────
    public List<Class> Classes         { get; set; } = new();
    public Guid?       SelectedClassId { get; set; }
    public Class?      SelectedClass   => Classes.FirstOrDefault(c => c.Id == SelectedClassId);

    /// <summary>overview | assignments | students | questions | work</summary>
    public string Tab    { get; set; } = "overview";
    /// <summary>all | practice | homework</summary>
    public string Type   { get; set; } = "all";
    /// <summary>all | 7d | 30d | 90d — by the date each student was given the work.</summary>
    public string Period { get; set; } = "all";

    // Work list (tab=work) and Questions tab selection.
    /// <summary>all | not-submitted | submitted | awaiting | graded | overdue</summary>
    public string WorkStatus       { get; set; } = "all";
    public Guid?  WorkAssignmentId { get; set; }
    public Guid?  WorkStudentId    { get; set; }
    public Guid?  QuestionAssignmentId { get; set; }

    public bool FiltersActive => Type != "all" || Period != "all";

    // ── What the class has ───────────────────────────────────────────────────
    /// <summary>Published (non-draft) assignments in the class before the type and period filters.</summary>
    public int ClassAssignmentCount { get; set; }

    public List<AnalyticsWork>          Work        { get; set; } = new();
    public List<AnalyticsAssignmentRow> Assignments { get; set; } = new();
    public List<AnalyticsStudentRow>    Students    { get; set; } = new();
    public List<AnalyticsAction>        Actions     { get; set; } = new();

    // Questions tab: the chosen assignment's questions.
    public List<AnalyticsQuestionRow>   Questions   { get; set; } = new();

    // ── Totals (every number is a count of Work items) ───────────────────────
    public int Assigned       => Work.Count;
    public int NotSubmitted   => Work.Count(w => w.Status == AnalyticsWorkStatus.NotSubmitted);
    public int AwaitingReview => Work.Count(w => w.Status == AnalyticsWorkStatus.AwaitingReview);
    public int Graded         => Work.Count(w => w.Status == AnalyticsWorkStatus.Graded);
    public int Submitted      => AwaitingReview + Graded;
    public int Overdue        => Work.Count(w => w.Overdue);

    /// <summary>Mean of the graded scores, or null while nothing is graded.</summary>
    public int? AverageScore  => Graded == 0 ? null
        : (int)Math.Round(Work.Where(w => w.Status == AnalyticsWorkStatus.Graded).Average(w => w.Score!.Value), MidpointRounding.AwayFromZero);

    public int? MedianScore
    {
        get
        {
            var s = Work.Where(w => w.Status == AnalyticsWorkStatus.Graded).Select(w => w.Score!.Value).OrderBy(v => v).ToList();
            if (s.Count == 0) return null;
            return s.Count % 2 == 1 ? s[s.Count / 2] : (int)Math.Round((s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>The work list as filtered by <see cref="WorkStatus"/>, assignment and student.</summary>
    public List<AnalyticsWork> FilteredWork => Work
        .Where(w => WorkAssignmentId is null || w.AssignmentId == WorkAssignmentId)
        .Where(w => WorkStudentId is null || w.StudentId == WorkStudentId)
        .Where(w => Matches(w, WorkStatus))
        .OrderBy(w => w.Status == AnalyticsWorkStatus.AwaitingReview ? 0 : w.Overdue ? 1 : 2)
        .ThenBy(w => w.Due ?? DateTime.MaxValue)
        .ThenBy(w => w.AssignmentTitle).ThenBy(w => w.StudentName)
        .ToList();

    public static bool Matches(AnalyticsWork w, string status) => status switch
    {
        "not-submitted" => w.Status == AnalyticsWorkStatus.NotSubmitted,
        "submitted"     => w.Status != AnalyticsWorkStatus.NotSubmitted,
        "awaiting"      => w.Status == AnalyticsWorkStatus.AwaitingReview,
        "graded"        => w.Status == AnalyticsWorkStatus.Graded,
        "overdue"       => w.Overdue,
        _               => true
    };

    public static string StatusTitle(string status) => status switch
    {
        "not-submitted" => "Not submitted",
        "submitted"     => "Submitted",
        "awaiting"      => "Awaiting review",
        "graded"        => "Graded",
        "overdue"       => "Overdue",
        _               => "All assigned work"
    };

    public static string TypeLabel(AssignmentType t) => t == AssignmentType.Homework ? "Homework" : "Classroom Practice";

    /// <summary>
    /// A link to this page with the class, type and period kept and the rest set as given — so every counter,
    /// chart segment and tab is an ordinary address the browser's Back button returns to.
    /// </summary>
    public string Link(string tab, string? status = null, Guid? assignmentId = null, Guid? studentId = null,
                       string? type = null, string? period = null, Guid? questionAssignment = null)
    {
        var q = new List<KeyValuePair<string, string?>>();
        if (SelectedClassId is { } c) q.Add(new("classId", c.ToString()));
        if (tab != "overview") q.Add(new("tab", tab));
        var t = type ?? Type;     if (t != "all") q.Add(new("type", t));
        var p = period ?? Period; if (p != "all") q.Add(new("period", p));
        if (status is not null && status != "all") q.Add(new("status", status));
        if (assignmentId is { } a) q.Add(new("assignmentId", a.ToString()));
        if (studentId is { } s) q.Add(new("studentId", s.ToString()));
        if (questionAssignment is { } qa) q.Add(new("questionAssignment", qa.ToString()));
        return "/Analytics" + QueryString.Create(q);
    }
}

/// <summary>Where one assignment given to one student stands. Exactly one applies.</summary>
public enum AnalyticsWorkStatus { NotSubmitted, AwaitingReview, Graded }

/// <summary>One assignment given to one student (a row of AssignmentStudents) and where it stands.</summary>
public class AnalyticsWork
{
    public Guid           AssignmentId    { get; set; }
    public string         AssignmentTitle { get; set; } = string.Empty;
    public AssignmentType AssignmentType  { get; set; }
    public Guid           StudentId       { get; set; }
    public string         StudentName     { get; set; } = string.Empty;
    public DateTime       AssignedAt      { get; set; }

    /// <summary>The student's own due date when they have one, otherwise the assignment's.</summary>
    public DateTime?      Due             { get; set; }
    public bool           PersonalDue     { get; set; }

    public AnalyticsWorkStatus Status     { get; set; }

    /// <summary>The newest submitted attempt — the one the gradebook, Student Work and the CSV show.</summary>
    public Guid?          LatestResultId  { get; set; }
    /// <summary>Its score; counted in averages only once <see cref="Status"/> is Graded.</summary>
    public int?           Score           { get; set; }
    public int            SubmittedAttempts { get; set; }
    public bool           DraftOpen       { get; set; }
    /// <summary>The AI is still marking the newest attempt (it is awaiting review until it finishes).</summary>
    public bool           MarkingPending  { get; set; }
    /// <summary>Not submitted and past the due date (the student's own, if set).</summary>
    public bool           Overdue         { get; set; }
}

/// <summary>One assignment on the Assignments tab and in the "Average score by assignment" chart.</summary>
public class AnalyticsAssignmentRow
{
    public Guid           Id          { get; set; }
    public string         Title       { get; set; } = string.Empty;
    public AssignmentType Type        { get; set; }
    public AssignmentStatus Status    { get; set; }
    public DateTime?      Deadline    { get; set; }
    public bool           HasPersonalDue { get; set; }
    public int            Recipients  { get; set; }
    public int            Submitted   { get; set; }
    public int            Awaiting    { get; set; }
    public int            Graded      { get; set; }
    public int            Overdue     { get; set; }
    public int?           AverageScore { get; set; }
}

/// <summary>One student on the Students tab.</summary>
public class AnalyticsStudentRow
{
    public Guid   StudentId { get; set; }
    public string FullName  { get; set; } = string.Empty;
    public string Email     { get; set; } = string.Empty;
    public int    Assigned  { get; set; }
    public int    Submitted { get; set; }
    public int    Awaiting  { get; set; }
    public int    Overdue   { get; set; }
    public int?   PracticeAverage { get; set; }
    public int    PracticeGraded  { get; set; }
    public int?   HomeworkAverage { get; set; }
    public int    HomeworkGraded  { get; set; }

    /// <summary>All graded work together, for the "below 50%" mark.</summary>
    public int?   OverallAverage  { get; set; }
}

/// <summary>A line of "Teacher actions": what to do and where it leads.</summary>
public class AnalyticsAction
{
    public string Icon   { get; set; } = "bx-check";
    public string Title  { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Button { get; set; } = string.Empty;
    public string Href   { get; set; } = string.Empty;
    public string Tone   { get; set; } = "primary";
}

/// <summary>One question (or part) of the chosen assignment on the Questions tab.</summary>
public class AnalyticsQuestionRow
{
    public int     Index     { get; set; }
    public string  Label     { get; set; } = string.Empty;
    public string  Title     { get; set; } = string.Empty;
    public bool    IsStem    { get; set; }
    public bool    IsPart    { get; set; }
    public decimal MaxPoints { get; set; }
    /// <summary>Answers handed in for it (newest submitted attempt of each recipient).</summary>
    public int     Answered  { get; set; }
    /// <summary>Of those, answers with a final mark (not waiting for the teacher or the AI).</summary>
    public int     GradedAnswers { get; set; }
    /// <summary>Mean points over the graded answers, or null when none is graded.</summary>
    public decimal? AveragePoints { get; set; }
    public List<AnalyticsAnswer> Answers { get; set; } = new();
}

/// <summary>One student's answer to a question, with links to the full work and any photo or drawing.</summary>
public class AnalyticsAnswer
{
    public Guid     ResultId    { get; set; }
    public Guid     StudentId   { get; set; }
    public string   StudentName { get; set; } = string.Empty;
    public string   Value       { get; set; } = string.Empty;
    public decimal? Points      { get; set; }
    public bool     Graded      { get; set; }
    public bool     NeedsReview { get; set; }
    public string?  ImagePath   { get; set; }
    public string?  SketchPath  { get; set; }
}
