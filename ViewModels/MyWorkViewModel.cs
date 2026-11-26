using SmartSchoolAI.Models;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.ViewModels;

/// <summary>The student's "My work" home page.</summary>
public class MyWorkViewModel
{
    public DateTime Now { get; set; }
    /// <summary>Classes the student is in (for the filter, and the empty states).</summary>
    public List<Class> Classes { get; set; } = new();
    public Guid? ClassId { get; set; }
    /// <summary>todo | submitted | feedback</summary>
    public string Section { get; set; } = "todo";
    public bool ShowAll { get; set; }
    public const int ShortList = 5;

    /// <summary>Everything in the chosen class (or all classes), before the section is applied.</summary>
    public List<StudentWorkItem> Work { get; set; } = new();
    public List<StudentWorkItem> InSection => Work.Where(w => w.Section == Section).ToList();
    public List<StudentWorkItem> Shown => ShowAll ? InSection : InSection.Take(ShortList).ToList();
    public int Count(string section) => Work.Count(w => w.Section == section);

    /// <summary>The first piece of work the student can do now (start or continue).</summary>
    public StudentWorkItem? Next => Work.FirstOrDefault(w => w.Actionable);

    /// <summary>Newest results the student may see, each opening that very attempt.</summary>
    public List<StudentWorkItem> RecentFeedback => Work.Where(w => w.LatestFeedback is not null)
        .OrderByDescending(w => w.LatestFeedback!.TeacherReviewedAt ?? w.LatestFeedback.UpdatedAt ?? w.LatestFeedback.SubmittedAt)
        .Take(3).ToList();

    /// <summary>Mean over results the student may see (newest visible attempt per assignment); null = no grades yet.</summary>
    public int? Average
    {
        get
        {
            var scores = Work.Where(w => w.LatestFeedback is not null).Select(w => w.LatestFeedback!.Score).ToList();
            return scores.Count == 0 ? null : (int)Math.Round(scores.Average(), MidpointRounding.AwayFromZero);
        }
    }
    public int GradedCount => Work.Count(w => w.LatestFeedback is not null);

    public string Link(string? section = null, Guid? classId = null, bool? all = null, bool clearClass = false)
    {
        var q = new List<string>();
        // The section is always named, so a bare "/Student" means "arrived from elsewhere" (restore the last view).
        q.Add($"section={section ?? Section}");
        var c = clearClass ? null : classId ?? ClassId; if (c is { } id) q.Add($"classId={id}");
        if (all ?? false) q.Add("all=1");
        return "/Student" + (q.Count > 0 ? "?" + string.Join("&", q) : "");
    }
}
