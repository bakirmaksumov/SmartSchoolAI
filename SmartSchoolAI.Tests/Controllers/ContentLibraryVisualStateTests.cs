using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Models;
using SmartSchoolAI.Tests.TestHelpers;

namespace SmartSchoolAI.Tests.Controllers;

/// <summary>
/// Regression tests for Question.VisualState — the interactive answer-panel
/// configuration (graph points, number-line marks, table cells, …) that used to
/// be silently dropped by model binding on save. See the Create-from-Scratch
/// investigation: every "visual" question type lost its panel state on Create,
/// and the Edit page's Table/Vertical/Fraction panels render with baked-in
/// default values (not the question's real saved state), so wiring them up
/// carelessly would have overwritten real answers with those defaults on any
/// unrelated edit (e.g. just changing difficulty).
/// </summary>
public class ContentLibraryVisualStateTests : IDisposable
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

    private async Task<Question> SeedQuestionAsync(
        string ownerEmail, QuestionType type, string? visualState)
    {
        var question = new Question
        {
            Title          = "Existing visual question",
            Type           = type,
            AnswerKey      = "some answer",
            CreatedByEmail = ownerEmail,
            VisualState    = visualState
        };
        _factory.Context.Questions.Add(question);
        await _factory.Context.SaveChangesAsync();
        return question;
    }

    // ── Create: every visual type's panel state must be saved ──────────────────

    [Fact]
    public async Task Create_Graphing_PersistsGraphState()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var controller = new ContentLibraryController(_factory.Context, _env);
        const string graphState = "{\"xMin\":-10,\"xMax\":10,\"yMin\":-10,\"yMax\":10,\"elements\":[]}";
        ControllerTestContext.SignIn(controller, owner.Id, owner.Email,
            form: new Dictionary<string, string> { ["graphState"] = graphState });

        await controller.Create(
            "Plot the line", QuestionType.Graphing, DifficultyLevel.Medium,
            null, null, "y=2x+1", null, false, null, null);

        var saved = await _factory.Context.Questions.SingleAsync(q => q.CreatedByEmail == owner.Email);
        Assert.Equal(graphState, saved.VisualState);
    }

    [Fact]
    public async Task Create_TableCheck_ChunksCellsIntoRowsMatchingHeaderCount()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var controller = new ContentLibraryController(_factory.Context, _env);
        ControllerTestContext.SignIn(controller, owner.Id, owner.Email,
            multiForm: new Dictionary<string, string[]>
            {
                ["tableHeaders"] = new[] { "x", "y" },
                ["tableCell"]    = new[] { "5", "10", "15", "20" }
            });

        await controller.Create(
            "Complete the table", QuestionType.TableCheck, DifficultyLevel.Medium,
            null, null, "5,10;15,20", null, false, null, null);

        var saved = await _factory.Context.Questions.SingleAsync(q => q.CreatedByEmail == owner.Email);
        Assert.Contains("\"headers\":[\"x\",\"y\"]", saved.VisualState);
        Assert.Contains("[\"5\",\"10\"]", saved.VisualState);
        Assert.Contains("[\"15\",\"20\"]", saved.VisualState);
    }

    // ── Edit: the panels this page doesn't really support must never overwrite ─

    [Theory]
    [InlineData(QuestionType.TableCheck)]
    [InlineData(QuestionType.VerticalOperation)]
    [InlineData(QuestionType.FractionModel)]
    public async Task Edit_TypeWithUnreliableEditPanel_NeverOverwritesExistingVisualState(QuestionType type)
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        const string real = "{\"real\":\"answer-grid\"}";
        var question = await SeedQuestionAsync(owner.Email, type, real);

        var controller = new ContentLibraryController(_factory.Context, _env);
        // Simulates the Edit page's actual default POST for these panels: table
        // cells present-but-blank, fraction/vertical fields at their baked-in
        // defaults — exactly what ships when the teacher never touched the panel.
        ControllerTestContext.SignIn(controller, owner.Id, owner.Email,
            form: new Dictionary<string, string>
            {
                ["fracRows"] = "2", ["fracCols"] = "4", ["fracShaded"] = "3", ["vertOp"] = "+"
            },
            multiForm: new Dictionary<string, string[]>
            {
                ["tableHeaders"] = new[] { "", "" },
                ["tableCell"]    = new[] { "", "", "", "" },
                ["vertOperands"] = new[] { "", "" }
            });

        await controller.Edit(
            question.Id, "Existing visual question", type, DifficultyLevel.Hard,
            null, null, "some answer", null, false, null, null);

        var updated = await _factory.Context.Questions.FindAsync(question.Id);
        Assert.Equal(real, updated!.VisualState);
        Assert.Equal(DifficultyLevel.Hard, updated.Difficulty); // the actual edit still went through
    }

    [Fact]
    public async Task Edit_NumberLine_LegacyFields_TranslateToMinMaxStepShape()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        var question = await SeedQuestionAsync(owner.Email, QuestionType.NumberLine, visualState: null);

        var controller = new ContentLibraryController(_factory.Context, _env);
        ControllerTestContext.SignIn(controller, owner.Id, owner.Email,
            form: new Dictionary<string, string>
            {
                ["nlStart"] = "0", ["nlEnd"] = "10", ["nlStep"] = "1", ["nlAnswer"] = "4"
            });

        await controller.Edit(
            question.Id, "Existing visual question", QuestionType.NumberLine, DifficultyLevel.Easy,
            null, null, "4", null, false, null, null);

        var updated = await _factory.Context.Questions.FindAsync(question.Id);
        Assert.Contains("\"min\":0", updated!.VisualState);
        Assert.Contains("\"max\":10", updated.VisualState);
        Assert.Contains("\"step\":1", updated.VisualState);
    }

    [Fact]
    public async Task Edit_GraphingType_HasNoPanelOnEditPage_PreservesExistingVisualState()
    {
        var owner = await SeedTeacherAsync("owner@school.test");
        const string real = "{\"xMin\":-10,\"xMax\":10,\"yMin\":-10,\"yMax\":10,\"elements\":[{\"type\":\"point\",\"pts\":[[1,1]]}]}";
        var question = await SeedQuestionAsync(owner.Email, QuestionType.Graphing, real);

        var controller = new ContentLibraryController(_factory.Context, _env);
        // The Edit page has no canvas for Graphing at all — nothing posts a
        // graphState field — so this is just a plain difficulty change.
        ControllerTestContext.SignIn(controller, owner.Id, owner.Email,
            form: new Dictionary<string, string>());

        await controller.Edit(
            question.Id, "Existing visual question", QuestionType.Graphing, DifficultyLevel.Hard,
            null, null, "some answer", null, false, null, null);

        var updated = await _factory.Context.Questions.FindAsync(question.Id);
        Assert.Equal(real, updated!.VisualState);
    }
}
