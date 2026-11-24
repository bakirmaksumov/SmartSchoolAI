using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Models;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Controllers;

/// <summary>
/// Regression tests for the IDOR fixes in ContentLibraryController: a teacher must
/// only be able to read/edit/delete/attach their own library questions and only
/// attach content to assignments they own. See SECURITY_AUDIT_2026-08-03.md items 1, 4, 5.
/// </summary>
public class ContentLibraryControllerTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    private readonly FakeWebHostEnvironment _env = new();

    public void Dispose()
    {
        _factory.Dispose();
        _env.Dispose();
    }

    private async Task<User> SeedTeacherAsync(string email)
    {
        var role = await _factory.Context.Roles.FirstAsync(r => r.Name == Role.Teacher);
        var user = new User { Email = email, PasswordHash = "x", FullName = "Teacher", RoleId = role.Id };
        _factory.Context.Users.Add(user);
        await _factory.Context.SaveChangesAsync();
        return user;
    }

    private async Task<Question> SeedQuestionAsync(string ownerEmail)
    {
        var question = new Question
        {
            Title          = "2 + 2 = ?",
            AnswerKey      = "4",
            CreatedByEmail = ownerEmail
        };
        _factory.Context.Questions.Add(question);
        await _factory.Context.SaveChangesAsync();
        return question;
    }

    private async Task<Assignment> SeedAssignmentAsync(Guid teacherId)
    {
        var cls = new Class
        {
            Name = "5A", Subject = "Math", AcademicYear = "2026",
            InviteCode = Guid.NewGuid().ToString("N")[..6].ToUpper(), TeacherId = teacherId
        };
        _factory.Context.Classes.Add(cls);
        await _factory.Context.SaveChangesAsync();

        var assignment = new Assignment { Title = "Homework 1", TeacherId = teacherId, ClassId = cls.Id };
        _factory.Context.Assignments.Add(assignment);
        await _factory.Context.SaveChangesAsync();
        return assignment;
    }

    private ContentLibraryController BuildController(Guid userId, string email)
    {
        var controller = new ContentLibraryController(_factory.Context, _env);
        ControllerTestContext.SignIn(controller, userId, email);
        return controller;
    }

    [Fact]
    public async Task Edit_Get_OwnQuestion_ReturnsView()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var question = await SeedQuestionAsync(owner.Email);
        var controller = BuildController(owner.Id, owner.Email);

        var result = await controller.Edit(question.Id);

        Assert.IsType<ViewResult>(result);
    }

    [Fact]
    public async Task Edit_Get_OtherTeachersQuestion_ReturnsNotFound()
    {
        var owner   = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var question = await SeedQuestionAsync(owner.Email);
        var controller = BuildController(intruder.Id, intruder.Email);

        var result = await controller.Edit(question.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Edit_Post_OtherTeachersQuestion_ReturnsNotFoundAndDoesNotModify()
    {
        var owner    = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var question = await SeedQuestionAsync(owner.Email);
        var controller = BuildController(intruder.Id, intruder.Email);

        var result = await controller.Edit(
            question.Id, "Tampered title", QuestionType.ShortAnswer, DifficultyLevel.Easy,
            null, null, "tampered-answer", null, false, null, null);

        Assert.IsType<NotFoundResult>(result);
        var unchanged = await _factory.Context.Questions.FindAsync(question.Id);
        Assert.Equal("2 + 2 = ?", unchanged!.Title);
        Assert.Equal("4", unchanged.AnswerKey);
    }

    [Fact]
    public async Task Delete_OwnQuestion_SoftDeletesIt()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var question = await SeedQuestionAsync(owner.Email);
        var controller = BuildController(owner.Id, owner.Email);

        await controller.Delete(question.Id);

        var deleted = await _factory.Context.Questions
            .IgnoreQueryFilters()
            .FirstAsync(q => q.Id == question.Id);
        Assert.NotNull(deleted.DeletedAt);
    }

    [Fact]
    public async Task Delete_OtherTeachersQuestion_ReturnsNotFoundAndDoesNotDelete()
    {
        var owner    = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var question = await SeedQuestionAsync(owner.Email);
        var controller = BuildController(intruder.Id, intruder.Email);

        var result = await controller.Delete(question.Id);

        Assert.IsType<NotFoundResult>(result);
        var stillThere = await _factory.Context.Questions.FindAsync(question.Id);
        Assert.Null(stillThere!.DeletedAt);
    }

    [Fact]
    public async Task AttachToAssignment_OwnQuestionAndAssignment_AttachesSuccessfully()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var question   = await SeedQuestionAsync(owner.Email);
        var assignment = await SeedAssignmentAsync(owner.Id);
        var controller = BuildController(owner.Id, owner.Email);

        var result = await controller.AttachToAssignment(question.Id, assignment.Id);

        Assert.IsType<RedirectToActionResult>(result);
        var updated = await _factory.Context.Assignments.FindAsync(assignment.Id);
        // Deserialize rather than substring-match the raw JSON: System.Text.Json's
        // default encoder escapes characters like '+' (e.g. "2 + 2") as a
        // defense-in-depth measure against XSS when this JSON is later embedded
        // via @Html.Raw in the assignment editor view.
        var attached = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(updated!.Questions);
        Assert.Single(attached!);
        Assert.Equal(question.Title, attached![0].GetProperty("title").GetString());
        Assert.Equal(question.Id.ToString(), attached[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task AttachToAssignment_OtherTeachersAssignment_ReturnsNotFoundAndDoesNotAttach()
    {
        var owner    = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var question   = await SeedQuestionAsync(intruder.Email);
        var assignment = await SeedAssignmentAsync(owner.Id); // belongs to "owner", not "intruder"
        var controller = BuildController(intruder.Id, intruder.Email);

        var result = await controller.AttachToAssignment(question.Id, assignment.Id);

        Assert.IsType<NotFoundResult>(result);
        var unchanged = await _factory.Context.Assignments.FindAsync(assignment.Id);
        Assert.Equal("[]", unchanged!.Questions);
    }

    [Fact]
    public async Task AttachToAssignment_OtherTeachersQuestion_ReturnsNotFoundAndDoesNotAttach()
    {
        var owner    = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var question   = await SeedQuestionAsync(owner.Email);   // not intruder's
        var assignment = await SeedAssignmentAsync(intruder.Id);
        var controller = BuildController(intruder.Id, intruder.Email);

        var result = await controller.AttachToAssignment(question.Id, assignment.Id);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Create_WithAssignmentIdOfAnotherTeacher_DoesNotAttachToIt()
    {
        var owner    = await SeedTeacherAsync("owner@school.test");
        var intruder = await SeedTeacherAsync("intruder@school.test");
        var foreignAssignment = await SeedAssignmentAsync(owner.Id);

        var controller = new ContentLibraryController(_factory.Context, _env);
        ControllerTestContext.SignIn(
            controller, intruder.Id, intruder.Email,
            form: new Dictionary<string, string> { ["assignmentId"] = foreignAssignment.Id.ToString() });

        await controller.Create(
            "New question", QuestionType.ShortAnswer, DifficultyLevel.Easy,
            null, null, "answer", null, false, null, null);

        var unchanged = await _factory.Context.Assignments.FindAsync(foreignAssignment.Id);
        Assert.Equal("[]", unchanged!.Questions);
    }

    [Fact]
    public void Controller_IsRestrictedToTeacherRole()
    {
        // Guards against the class-level [Authorize(Roles = "Teacher")] being
        // accidentally removed or narrowed back down to a bare [Authorize],
        // which is exactly what let students read AnswerKey via GetQuestions.
        var attribute = typeof(ContentLibraryController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault();

        Assert.NotNull(attribute);
        Assert.Equal("Teacher", attribute!.Roles);
    }
}
