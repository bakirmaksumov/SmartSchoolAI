using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Models;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;

namespace SmartSchoolAI.Tests.Controllers;

/// <summary>
/// "Add from library" in the assignment editor (POST /Assignments/ImportFromLibrary) follows the Content Library's
/// rule: a teacher may add their own questions and the ones shared with all teachers, never another teacher's
/// private one — and only into an assignment they may edit. One unavailable question refuses the whole batch, and the
/// assignment is left exactly as it was.
/// </summary>
public class ImportFromLibraryAccessTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    public void Dispose() => _factory.Dispose();


    private async Task<User> TeacherAsync(string email)
    {
        var role = await _factory.Context.Roles.FirstAsync(r => r.Name == Role.Teacher);
        var u = new User { Email = email, PasswordHash = "x", FullName = email, RoleId = role.Id };
        _factory.Context.Users.Add(u);
        await _factory.Context.SaveChangesAsync();
        return u;
    }

    private async Task<Assignment> AssignmentAsync(User owner, string existingQuestionsJson = "[]")
    {
        var cls = new Class { Name = "A " + owner.Email, Subject = "Math", AcademicYear = "2026",
                              InviteCode = Guid.NewGuid().ToString("N")[..6], TeacherId = owner.Id };
        _factory.Context.Classes.Add(cls);
        var a = new Assignment { Title = "Homework", TeacherId = owner.Id, ClassId = cls.Id, Questions = existingQuestionsJson };
        _factory.Context.Assignments.Add(a);
        await _factory.Context.SaveChangesAsync();
        return a;
    }

    private async Task<Question> QuestionAsync(User author, string title, bool isPublic = false)
    {
        var q = new Question { Title = title, AnswerKey = "1", CreatedByEmail = author.Email, IsPublic = isPublic };
        _factory.Context.Questions.Add(q);
        await _factory.Context.SaveChangesAsync();
        return q;
    }

    private AssignmentsController Editor(User t)
    {
        // ImportFromLibrary uses only the database; the editor's other services are not needed here.
        var c = new AssignmentsController(_factory.Context, null!, null!, null!, null!, null!, null!);
        ControllerTestContext.SignIn(c, t.Id, t.Email);
        return c;
    }

    private Task<IActionResult> Import(User t, Guid assignmentId, params Guid[] ids)
        => Editor(t).ImportFromLibrary(assignmentId, JsonSerializer.Serialize(ids));

    private async Task<List<string>> TitlesIn(Guid assignmentId)
    {
        var json = (await _factory.Context.Assignments.AsNoTracking().SingleAsync(a => a.Id == assignmentId)).Questions ?? "[]";
        return JsonSerializer.Deserialize<List<JsonElement>>(json)!.Select(e => e.GetProperty("title").GetString()!).ToList();
    }

    [Fact]
    public async Task Own_question_is_added()
    {
        var t = await TeacherAsync("t@school.test");
        var a = await AssignmentAsync(t);
        var mine = await QuestionAsync(t, "Mine");

        var r = Assert.IsType<JsonResult>(await Import(t, a.Id, mine.Id));
        Assert.Contains("added = 1", r.Value!.ToString());
        Assert.Equal(new[] { "Mine" }, await TitlesIn(a.Id));
    }

    [Fact]
    public async Task Question_shared_with_all_teachers_is_added()
    {
        var t = await TeacherAsync("t@school.test");
        var other = await TeacherAsync("other@school.test");
        var a = await AssignmentAsync(t);
        var shared = await QuestionAsync(other, "Shared by a colleague", isPublic: true);

        Assert.IsType<JsonResult>(await Import(t, a.Id, shared.Id));
        Assert.Equal(new[] { "Shared by a colleague" }, await TitlesIn(a.Id));
    }

    [Fact]
    public async Task Another_teachers_private_question_is_refused()
    {
        var t = await TeacherAsync("t@school.test");
        var other = await TeacherAsync("other@school.test");
        var a = await AssignmentAsync(t);
        var secret = await QuestionAsync(other, "Private to a colleague");

        Assert.IsType<NotFoundObjectResult>(await Import(t, a.Id, secret.Id));
        Assert.Empty(await TitlesIn(a.Id));
    }

    [Fact]
    public async Task Deleted_or_unknown_question_is_refused_like_a_private_one()
    {
        var t = await TeacherAsync("t@school.test");
        var a = await AssignmentAsync(t);
        var gone = await QuestionAsync(t, "Deleted");
        gone.DeletedAt = DateTime.UtcNow;
        await _factory.Context.SaveChangesAsync();

        Assert.IsType<NotFoundObjectResult>(await Import(t, a.Id, gone.Id));
        Assert.IsType<NotFoundObjectResult>(await Import(t, a.Id, Guid.NewGuid()));
        Assert.Empty(await TitlesIn(a.Id));
    }

    [Fact]
    public async Task Assignment_the_teacher_cannot_edit_is_refused_and_left_unchanged()
    {
        var t = await TeacherAsync("t@school.test");
        var other = await TeacherAsync("other@school.test");
        var theirs = await AssignmentAsync(other);
        var mine = await QuestionAsync(t, "Mine", isPublic: true);

        // Someone else's assignment (not owner, not co-teacher).
        Assert.IsType<NotFoundObjectResult>(await Import(t, theirs.Id, mine.Id));
        Assert.Empty(await TitlesIn(theirs.Id));

        // An own assignment whose questions are frozen (closed).
        var closed = await AssignmentAsync(t);
        closed.Status = AssignmentStatus.Closed;
        await _factory.Context.SaveChangesAsync();
        Assert.IsType<NotFoundObjectResult>(await Import(t, closed.Id, mine.Id));
        Assert.Empty(await TitlesIn(closed.Id));
    }

    [Fact]
    public async Task Co_teacher_may_add_into_the_class_assignment()
    {
        var owner = await TeacherAsync("owner@school.test");
        var co = await TeacherAsync("co@school.test");
        var a = await AssignmentAsync(owner);
        _factory.Context.ClassTeachers.Add(new ClassTeacher { ClassId = a.ClassId, TeacherId = co.Id });
        await _factory.Context.SaveChangesAsync();
        var coQuestion = await QuestionAsync(co, "Co-teacher's own");

        Assert.IsType<JsonResult>(await Import(co, a.Id, coQuestion.Id));
        Assert.Equal(new[] { "Co-teacher's own" }, await TitlesIn(a.Id));
    }

    [Fact]
    public async Task Mixed_batch_with_one_unavailable_question_adds_nothing()
    {
        var t = await TeacherAsync("t@school.test");
        var other = await TeacherAsync("other@school.test");
        var existing = """[{"id":"11111111-1111-1111-1111-111111111111","title":"Already there","type":0}]""";
        var a = await AssignmentAsync(t, existing);
        var mine = await QuestionAsync(t, "Mine");
        var shared = await QuestionAsync(other, "Shared", isPublic: true);
        var secret = await QuestionAsync(other, "Private");
        var updatedBefore = a.UpdatedAt;

        var r = Assert.IsType<NotFoundObjectResult>(await Import(t, a.Id, mine.Id, shared.Id, secret.Id));
        Assert.Contains("Nothing was added", r.Value!.ToString());
        Assert.Equal(new[] { "Already there" }, await TitlesIn(a.Id));
        Assert.Equal(updatedBefore, (await _factory.Context.Assignments.AsNoTracking().SingleAsync(x => x.Id == a.Id)).UpdatedAt);

        // The same batch without the private question goes through, in the order chosen.
        Assert.IsType<JsonResult>(await Import(t, a.Id, mine.Id, shared.Id));
        Assert.Equal(new[] { "Already there", "Mine", "Shared" }, await TitlesIn(a.Id));
    }
}
