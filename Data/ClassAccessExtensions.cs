using SmartSchoolAI.Models;

namespace SmartSchoolAI.Data;

/// <summary>
/// Centralises the "owner or co-teacher" access rule so every controller checks
/// class/assignment ownership the same way once co-teachers are involved.
/// </summary>
public static class ClassAccessExtensions
{
    public static IQueryable<Class> AccessibleBy(this IQueryable<Class> classes, Guid userId)
        => classes.Where(c => c.TeacherId == userId || c.CoTeachers.Any(ct => ct.TeacherId == userId));

    public static IQueryable<Assignment> AccessibleBy(this IQueryable<Assignment> assignments, Guid userId)
        => assignments.Where(a => a.TeacherId == userId
                                || a.Class.TeacherId == userId
                                || a.Class.CoTeachers.Any(ct => ct.TeacherId == userId));
}
