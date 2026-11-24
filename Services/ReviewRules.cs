using System.Linq.Expressions;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// The one definition of "needs the teacher's review": a submitted attempt with at least one answer the AI
/// could not confirm (<see cref="StudentAnswer.NeedsReview"/>; drawn answers always start flagged). Grading the
/// submission clears the flags. The dashboard counter, the review queue and the "Needs review" filter all use it.
/// </summary>
public static class ReviewRules
{
    public static readonly Expression<Func<StudentResult, bool>> NeedsReview =
        r => r.IsSubmitted && r.StudentAnswers.Any(a => a.NeedsReview);

    private static readonly Func<StudentResult, bool> Compiled = NeedsReview.Compile();

    /// <summary>The same rule for a result already loaded with its answers.</summary>
    public static bool Needs(StudentResult result) => Compiled(result);
}
