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
            .Select(r => new { r.AssignmentId, r.StudentId, r.Score, r.AttemptNumber })
            .ToListAsync();

        foreach (var row in assigned)
            if (stats.TryGetValue((assignmentToClass[row.AssignmentId], row.StudentId), out var stat))
                stat.Assigned++;

        // Counted per assignment, as on the assignment page: an assignment is submitted once the student has a saved
        // final attempt for it (retakes do not count twice), and only assignments the student was given count.
        // The average uses each assignment's newest submitted attempt.
        var given = assigned.Select(x => (x.AssignmentId, x.StudentId)).ToHashSet();
        foreach (var group in submitted.Where(r => given.Contains((r.AssignmentId, r.StudentId)))
                                       .GroupBy(r => (assignmentToClass[r.AssignmentId], r.StudentId)))
        {
            if (!stats.TryGetValue(group.Key, out var stat)) continue;
            var perAssignment = group.GroupBy(r => r.AssignmentId).Select(g => g.OrderByDescending(r => r.AttemptNumber).First()).ToList();
            stat.Submitted    = perAssignment.Count;
            stat.AverageScore = (int)Math.Round(perAssignment.Average(r => r.Score));
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

        var recipients = await _db.AssignmentStudents
            .Where(x => assignmentIds.Contains(x.AssignmentId))
            .Select(x => new { x.AssignmentId, x.StudentId })
            .ToListAsync();
        var given = recipients.Select(x => (x.AssignmentId, x.StudentId)).ToHashSet();

        var results = await _db.StudentResults
            .Where(r => assignmentIds.Contains(r.AssignmentId) && r.IsSubmitted)
            .Select(r => new
            {
                r.AssignmentId,
                r.StudentId,
                r.AttemptNumber,
                r.Score,
                Flagged = r.StudentAnswers.Any(a => a.NeedsReview)
            })
            .ToListAsync();

        foreach (var row in recipients.GroupBy(x => x.AssignmentId))
            if (stats.TryGetValue(row.Key, out var stat)) stat.Assigned = row.Count();

        // The assignment page's rules: submitted = current recipients with a saved final attempt (students, not
        // attempts); the average over each one's newest submitted attempt; needs review = students with flagged work.
        foreach (var group in results.Where(r => given.Contains((r.AssignmentId, r.StudentId))).GroupBy(r => r.AssignmentId))
        {
            if (!stats.TryGetValue(group.Key, out var stat)) continue;
            var latest = group.GroupBy(r => r.StudentId).Select(g => g.OrderByDescending(r => r.AttemptNumber).First()).ToList();
            stat.Submitted    = latest.Count;
            stat.AverageScore = (int)Math.Round(latest.Average(r => r.Score));
            stat.NeedsReview  = group.Where(r => r.Flagged).Select(r => r.StudentId).Distinct().Count();
        }

        return stats;
    }
}
