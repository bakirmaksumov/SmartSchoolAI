using System.Security.Claims;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Data;

/// <summary>
/// The Content Library's sharing rule in one place: a teacher may use their own questions and the ones another
/// teacher shared with everyone ("Make visible to other teachers", <see cref="Question.IsPublic"/>). Another
/// teacher's private question is invisible — listing it, opening it or adding it to an assignment all go through here.
/// </summary>
public static class QuestionAccessExtensions
{
    /// <summary>Who owns a library question: questions store their author's email.</summary>
    public static string LibraryOwnerKey(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.Email) ?? user.Identity?.Name ?? "guest";

    public static IQueryable<Question> UsableBy(this IQueryable<Question> questions, string ownerKey)
        => questions.Where(q => q.CreatedByEmail == ownerKey || q.IsPublic);
}
