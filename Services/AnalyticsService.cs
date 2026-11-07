using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.ViewModels;

namespace SmartSchoolAI.Services;

/// <summary>
/// Builds the teacher's analytics page: loads the class's published work and
/// turns raw submissions into the summaries the page shows. Kept out of the
/// controller so the aggregation can be read, reused and tested on its own.
/// </summary>
public class AnalyticsService
{
    private readonly AppDbContext _db;

    public AnalyticsService(AppDbContext db) => _db = db;

    /// <summary>Time attributed to a single submission is capped so one forgotten tab can't skew the total.</summary>
    private const double MaxMinutesPerSubmission = 240;

    /// <summary>A student is "struggling" below this average.</summary>
    private const int StrugglingBelow = 50;

    public async Task<AnalyticsViewModel> BuildAsync(Guid teacherId, Guid? classId, string tab)
    {
        var model = new AnalyticsViewModel
        {
            Tab     = tab == "assessment" ? "assessment" : "practice",
            Classes = await _db.Classes
                .AccessibleBy(teacherId)
                .Where(c => !c.IsArchived)
                .OrderBy(c => c.Name)
                .ToListAsync()
        };

        // Default to the first class so the page is useful without picking one.
        model.SelectedClassId = classId ?? model.Classes.FirstOrDefault()?.Id;
        if (model.SelectedClassId is null) return model;      // teacher has no classes

        var assignments = await LoadAssignmentsAsync(teacherId, model.SelectedClassId.Value, model.Tab);
        var assignmentIds = assignments.Select(a => a.Id).ToList();

        var results = await _db.StudentResults
            .Include(r => r.Assignment)
            .Where(r => assignmentIds.Contains(r.AssignmentId) && r.IsSubmitted)
            .ToListAsync();

        var students = await _db.ClassStudents
            .Where(cs => cs.ClassId == model.SelectedClassId)
            .Select(cs => cs.Student)
            .OrderBy(s => s.FullName)
            .ToListAsync();

        model.HasData = results.Count > 0;

        BuildSummary(model, results);
        model.Assignments        = BuildAssignmentRows(assignments, results);
        model.StudentRows        = BuildStudentRows(students, assignments, results);
        model.StrugglingStudents = model.StudentRows
            .Where(s => s.AverageScore >= 0 && s.AverageScore < StrugglingBelow)
            .OrderBy(s => s.AverageScore)
            .Cast<AnalyticsStudentSummary>()
            .ToList();

        return model;
    }

    private Task<List<Assignment>> LoadAssignmentsAsync(Guid teacherId, Guid classId, string tab)
    {
        var type = tab == "assessment" ? AssignmentType.Homework : AssignmentType.ClassroomPractice;

        return _db.Assignments
            .AccessibleBy(teacherId)
            .Where(a => a.ClassId == classId
                     && a.Type == type
                     && a.Status != AssignmentStatus.Draft)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync();
    }

    private static void BuildSummary(AnalyticsViewModel model, List<StudentResult> results)
    {
        var scores = results.Select(r => r.Score).ToList();

        model.TotalSubmissions = results.Count;
        model.AverageScore     = scores.Count > 0 ? (int)Math.Round(scores.Average()) : 0;
        model.MedianScore      = Median(scores);

        // Rough time-on-task: from when the work was set to when it came in.
        var minutes = results.Sum(r =>
            Math.Min((r.SubmittedAt - r.Assignment.CreatedAt).TotalMinutes, MaxMinutesPerSubmission));

        model.TotalHours   = (int)(minutes / 60);
        model.TotalMinutes = (int)(minutes % 60);
    }

    private static List<AnalyticsAssignmentRow> BuildAssignmentRows(
        List<Assignment> assignments, List<StudentResult> results) =>
        assignments.Select(a =>
        {
            var forAssignment = results.Where(r => r.AssignmentId == a.Id).ToList();
            return new AnalyticsAssignmentRow
            {
                Id           = a.Id,
                Title        = a.Title,
                CreatedAt    = a.CreatedAt,
                Submissions  = forAssignment.Count,
                AverageScore = forAssignment.Count > 0
                    ? (int)Math.Round(forAssignment.Average(r => r.Score))
                    : 0
            };
        }).ToList();

    private static List<AnalyticsStudentRow> BuildStudentRows(
        List<User> students, List<Assignment> assignments, List<StudentResult> results) =>
        students.Select(s =>
        {
            var mine = results.Where(r => r.StudentId == s.Id).ToList();

            return new AnalyticsStudentRow
            {
                StudentId    = s.Id,
                FullName     = s.FullName,
                Submissions  = mine.Count,
                AverageScore = mine.Count > 0 ? (int)Math.Round(mine.Average(r => r.Score)) : -1,
                Cells = assignments.Select(a =>
                {
                    var result = mine.FirstOrDefault(r => r.AssignmentId == a.Id);
                    return new AnalyticsScoreCell
                    {
                        AssignmentId = a.Id,
                        Score        = result?.Score,
                        ResultId     = result?.Id
                    };
                }).ToList()
            };
        }).ToList();

    private static int Median(List<int> values)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var mid    = sorted.Count / 2;

        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
