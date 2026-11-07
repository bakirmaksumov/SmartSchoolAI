using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.Services;

/// <summary>
/// Turns raw submissions into the participation and score summaries shown on
/// the class page and the student roster. Lives outside the controller so both
/// pages share one definition of "participation" and "average score".
/// </summary>
public class ClassStatisticsService
{
    private readonly AppDbContext _db;

    public ClassStatisticsService(AppDbContext db) => _db = db;

    /// <summary>
    /// Participation and average per (class, student) for several classes at once.
    /// Batched deliberately: the roster spans every class a teacher owns, and doing
    /// this per class would fire two queries per class.
    /// </summary>
    public async Task<Dictionary<(Guid ClassId, Guid StudentId), ClassStudentStatDto>>
        ForStudentsAsync(IReadOnlyCollection<Class> classes)
    {
        var stats = new Dictionary<(Guid, Guid), ClassStudentStatDto>();

        foreach (var cls in classes)
            foreach (var enrolment in cls.ClassStudents)
                stats[(cls.Id, enrolment.StudentId)] = new ClassStudentStatDto();

        var assignmentToClass = classes
            .SelectMany(c => c.Assignments.Select(a => new { a.Id, ClassId = c.Id }))
            .ToDictionary(x => x.Id, x => x.ClassId);

        if (assignmentToClass.Count == 0) return stats;

        var assignmentIds = assignmentToClass.Keys.ToList();

        var assigned = await _db.AssignmentStudents
            .Where(x => assignmentIds.Contains(x.AssignmentId))
            .Select(x => new { x.AssignmentId, x.StudentId })
            .ToListAsync();

        var submitted = await _db.StudentResults
            .Where(r => assignmentIds.Contains(r.AssignmentId) && r.IsSubmitted)
            .Select(r => new { r.AssignmentId, r.StudentId, r.Score })
            .ToListAsync();

        foreach (var row in assigned)
            if (stats.TryGetValue((assignmentToClass[row.AssignmentId], row.StudentId), out var stat))
                stat.Assigned++;

        foreach (var group in submitted.GroupBy(r => (assignmentToClass[r.AssignmentId], r.StudentId)))
        {
            if (!stats.TryGetValue(group.Key, out var stat)) continue;
            stat.Submitted    = group.Count();
            stat.AverageScore = (int)Math.Round(group.Average(r => r.Score));
        }

        return stats;
    }

    /// <summary>Participation and average per student for a single class.</summary>
    public async Task<Dictionary<Guid, ClassStudentStatDto>> ForStudentsAsync(Class cls)
    {
        var all = await ForStudentsAsync(new[] { cls });

        return all.Where(entry => entry.Key.ClassId == cls.Id)
                  .ToDictionary(entry => entry.Key.StudentId, entry => entry.Value);
    }

    /// <summary>How each of the class's assignments went across the students it was given to.</summary>
    public async Task<Dictionary<Guid, ClassAssignmentStatDto>> ForAssignmentsAsync(Class cls)
    {
        var assignmentIds = cls.Assignments.Select(a => a.Id).ToList();
        var stats = assignmentIds.ToDictionary(id => id, _ => new ClassAssignmentStatDto());

        if (assignmentIds.Count == 0) return stats;

        var assignedCounts = await _db.AssignmentStudents
            .Where(x => assignmentIds.Contains(x.AssignmentId))
            .GroupBy(x => x.AssignmentId)
            .Select(g => new { AssignmentId = g.Key, Count = g.Count() })
            .ToListAsync();

        var results = await _db.StudentResults
            .Where(r => assignmentIds.Contains(r.AssignmentId) && r.IsSubmitted)
            .Select(r => new
            {
                r.AssignmentId,
                r.Score,
                Flagged = r.StudentAnswers.Count(a => a.NeedsReview)
            })
            .ToListAsync();

        foreach (var row in assignedCounts)
            if (stats.TryGetValue(row.AssignmentId, out var stat)) stat.Assigned = row.Count;

        foreach (var group in results.GroupBy(r => r.AssignmentId))
        {
            if (!stats.TryGetValue(group.Key, out var stat)) continue;
            stat.Submitted    = group.Count();
            stat.AverageScore = (int)Math.Round(group.Average(r => r.Score));
            stat.NeedsReview  = group.Sum(r => r.Flagged);
        }

        return stats;
    }
}
