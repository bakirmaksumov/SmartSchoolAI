using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// Which model each kind of answer goes to, what it is sent, what is stored and what is never paid for twice —
/// against a stand-in OpenRouter that checks the model and the actual content of every request.
/// </summary>
public class GradingRoutingTests : IDisposable
{
    private const string Gemini = "google/gemini-3.1-pro-preview";
    private const string Gpt    = "openai/gpt-5.5";

    private readonly FileDb _db = new();
    private readonly FakeWebHostEnvironment _env = new();
    private readonly FakeOpenRouter _ai = new();
    private readonly IConfiguration _config;

    public GradingRoutingTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = "test-key",
            ["OpenRouter:BaseUrl"] = "https://openrouter.test/api/v1",
            ["OpenRouter:Model"] = "openai/gpt-4o",                       // the tutor / import / translation model: untouched
            ["OpenRouter:EnableGradingVerification"] = "true",
            ["OpenRouter:Grading:RecognitionModel"] = Gemini,
            ["OpenRouter:Grading:GradingModel"] = Gpt
        }).Build();

        _ai.Reply = r => r.Kind switch
        {
            "recognition"  => Recognised("2(x + 3) = 10", "2x + 6 = 10", "2x = 4", "x = 2"),
            "verification" => """{"overall":"correct","agrees":true,"confidence":0.95,"notes":"Agreed.","steps":[]}""",
            "typed"        => """[{"index":2,"correct":true,"marks":2,"feedback":"Clear.","criteria":[{"criterion":"B1 reason","maxMarks":1,"awarded":1,"explanation":"ok"},{"criterion":"B1 example","maxMarks":1,"awarded":1,"explanation":"ok"}]}]""",
            _              => FullMarks
        };
    }

    public void Dispose() { _db.Dispose(); _env.Dispose(); }

    // q0: a 3-mark solution with the teacher's scheme; q1: a choice with a key; q2: an open answer worth 2.
    private const string Questions = """
        [{"id":"q0","title":"Solve $2(x+3)=10$.","type":"0","answerKey":"x = 2","marks":3,
          "markScheme":"M1 expands the brackets; M1 isolates x; A1 x = 2"},
         {"id":"q1","title":"Which is $2+2$?","type":"4","options":"[\"3\",\"4\",\"5\"]","answerKey":"B"},
         {"id":"q2","title":"Explain why 0 is even.","type":"2","answerKey":"","marks":2}]
        """;

    private const string FullMarks = """
        {"criteria":[{"criterion":"M1 expands","maxMarks":1,"awarded":1,"explanation":"Correct.","evidence":"line 2: 2x + 6 = 10","step":2},
                     {"criterion":"M1 isolates x","maxMarks":1,"awarded":1,"explanation":"Correct.","evidence":"line 3: 2x = 4","step":3},
                     {"criterion":"A1 x = 2","maxMarks":1,"awarded":1,"explanation":"Correct.","evidence":"line 4: x = 2","step":4}],
         "awardedMarks":3,"maxMarks":3,
         "steps":[{"number":1,"status":"correct"},{"number":2,"status":"correct"},{"number":3,"status":"correct"},{"number":4,"status":"correct"}],
         "overall":"correct","expectedAnswer":"x = 2","summary":"All correct.","transcriptionIssues":[],"needsTeacher":false}
        """;

    private static string Recognised(params string[] lines) => JsonSerializer.Serialize(new
    {
        detectedProblem = "",
        steps = lines.Select((t, i) => new { number = i + 1, text = t, unclear = Array.Empty<object>() })
    });

    private static string StepsJson(params string[] lines)
        => JsonSerializer.Serialize(lines.Select((t, i) => new { number = i + 1, text = t }));

    private async Task<(Guid student, Assignment a)> SetupAsync()
    {
        var t = await _db.UserAsync(Role.Teacher, "T");
        var s = await _db.UserAsync(Role.Student, "S");
        var cls = await _db.ClassAsync(t.Id, s.Id);
        var a = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        await using var db = _db.NewContext();
        var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
        row.Questions = Questions;
        row.MaxAttempts = 5;
        db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s.Id });
        await db.SaveChangesAsync();
        return (s.Id, row);
    }

    private async Task<string> PhotoAsync(Guid student, string content)
        => JsonSerializer.Serialize(new[] { await SubmissionStorage.SaveAsync(_env, Encoding.UTF8.GetBytes(content), ".jpg", student) });

    private static string DataUri(string content) => "data:image/jpeg;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(content));

    private async Task<GradingService.StepsGradeOutcome> EvaluateAsync(Guid student, Guid assignment, string stepsJson, string imagePath, int q = 0)
    {
        await using var db = _db.NewContext();
        return await AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env).EvaluateStepsAsync(assignment, q, student, stepsJson, imagePath);
    }

    private async Task<AiMarkingService.Recognition> AnalyzeAsync(string content, string question = "Solve $2(x+3)=10$.")
        => await AiTestServices.Marking(_db.NewContext, _config, _ai).RecognizeAsync(
               new[] { new AiMarkingService.Page(Encoding.UTF8.GetBytes(content), "image/jpeg") }, question, new AiCallContext("recognition"));

    private async Task<StudentAnswer> AnswerAsync(Guid student, Guid assignment, int q = 0)
    {
        await using var db = _db.NewContext();
        return await db.StudentAnswers.Include(x => x.StudentResult)
            .SingleAsync(x => x.StudentResult.StudentId == student && x.StudentResult.AssignmentId == assignment && x.QuestionIndex == q);
    }

    // ── Routing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Photo_is_read_by_Gemini_then_marked_by_GPT_with_the_original_photo_and_the_reading()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: 2(x+3)=10 ...");

        // The student uploads the photo (reading), checks the steps, then sends them for marking.
        var reading = await AnalyzeAsync("PHOTO: 2(x+3)=10 ...");
        var outcome = await EvaluateAsync(s, a.Id, StepsJson(reading.Steps.Select(x => x.Text).ToArray()), photo);

        Assert.True(outcome.Success);
        var calls = _ai.All;
        Assert.Equal(new[] { "recognition", "grading", "verification" }, calls.Select(c => c.Kind));
        Assert.Equal(Gemini, calls[0].Model);
        Assert.Equal(DataUri("PHOTO: 2(x+3)=10 ..."), Assert.Single(calls[0].Images));
        Assert.Equal(Gpt, calls[1].Model);
        Assert.Contains(DataUri("PHOTO: 2(x+3)=10 ..."), calls[1].Images);                 // the original goes to the marker
        Assert.Contains("Lines as read from the photo", calls[1].Text);
        Assert.Contains("2x + 6 = 10", calls[1].Text);
        Assert.Contains("M1 expands the brackets; M1 isolates x; A1 x = 2", calls[1].Text); // the teacher's scheme
        Assert.Contains("Marks available: 3", calls[1].Text);
        Assert.Equal(Gpt, calls[2].Model);
        Assert.DoesNotContain(calls, c => c.Model == "openai/gpt-4o");                    // OpenRouter:Model is not used here

        var answer = await AnswerAsync(s, a.Id);
        Assert.Equal((3m, true), (answer.PointsAwarded!.Value, answer.IsCorrect!.Value));
        Assert.NotNull(answer.RecognitionKey);
        Assert.NotNull(answer.GradingKey);
        Assert.Equal(3, AiCostReport.Verdict(answer)!.Criteria.Count);
    }

    [Fact]
    public async Task Typed_answer_goes_straight_to_GPT_without_any_reading()
    {
        var (s, a) = await SetupAsync();
        await using (var db = _db.NewContext())
        {
            var submissions = new SubmissionService(db, AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env), _env, NullLogger<SubmissionService>.Instance);
            var saved = await submissions.SubmitAsync(s, a.Id, Guid.NewGuid(), new Dictionary<string, string> { ["q_1"] = "B", ["q_2"] = "0 = 2 × 0, so it is a multiple of 2." });
            Assert.True(saved.GradingPending);
            await submissions.GradeAfterSubmitAsync(saved.ResultId);
        }

        var call = Assert.Single(_ai.All);
        Assert.Equal(("typed", Gpt), (call.Kind, call.Model));
        Assert.Empty(call.Images);
        Assert.Contains("Explain why 0 is even.", call.Text);
        Assert.Contains("0 = 2 × 0", call.Text);
        Assert.DoesNotContain("Which is", call.Text);                       // the choice with a key is not sent

        var answer = await AnswerAsync(s, a.Id, 2);
        Assert.Equal(2m, answer.PointsAwarded);
        Assert.Equal(2, AiCostReport.Verdict(answer)!.Criteria.Count);
        Assert.Equal(1m, (await AnswerAsync(s, a.Id, 1)).PointsAwarded);    // B by the key
    }

    [Fact]
    public async Task A_choice_answered_by_the_key_costs_nothing()
    {
        var (s, a) = await SetupAsync();
        await using var db = _db.NewContext();
        var submissions = new SubmissionService(db, AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env), _env, NullLogger<SubmissionService>.Instance);
        var saved = await submissions.SubmitAsync(s, a.Id, Guid.NewGuid(), new Dictionary<string, string> { ["q_1"] = "B" });

        Assert.False(saved.GradingPending);
        await submissions.GradeAfterSubmitAsync(saved.ResultId);
        Assert.Empty(_ai.All);
        Assert.Empty(await _db.NewContext().AiCallLogs.ToListAsync());
    }

    // ── What the marker sees and decides ─────────────────────────────────────

    [Fact]
    public async Task A_minus_sign_lost_in_the_reading_reaches_the_marker_with_the_photo_and_goes_to_the_teacher()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO shows: x = -3");
        _ai.Reply = r => r.Kind switch
        {
            "recognition"  => Recognised("2x + 6 = 0", "x = 3"),                 // the reading dropped the minus
            "verification" => """{"overall":"correct","agrees":true,"confidence":0.9,"notes":"","steps":[]}""",
            _ => """
                {"criteria":[{"criterion":"M1 method","maxMarks":2,"awarded":2,"explanation":"Correct method.","evidence":"line 1","step":1},
                             {"criterion":"A1 x = -3","maxMarks":1,"awarded":1,"explanation":"The photo shows x = −3.","evidence":"line 2 on the photo: x = −3","step":2}],
                 "steps":[{"number":1,"status":"correct"},{"number":2,"status":"correct"}],"overall":"correct","summary":"Correct.",
                 "transcriptionIssues":[{"step":2,"transcribed":"3","onPhoto":"-3","certain":false,"marksAffected":1,"note":"faint sign"}],
                 "needsTeacher":true,"teacherNote":"Line 2: the sign before 3 is unclear. 1 mark depends on it."}
                """
        };

        var outcome = await EvaluateAsync(s, a.Id, StepsJson("2x + 6 = 0", "x = 3"), photo);

        Assert.True(outcome.Success);
        var grading = _ai.All.Single(c => c.Kind == "grading");
        Assert.Contains(DataUri("PHOTO shows: x = -3"), grading.Images);   // the marker can see the minus on the original
        Assert.Contains("x = 3", grading.Text);                             // …next to what the reading said
        Assert.Contains("The photo is the authority", grading.System);

        var answer = await AnswerAsync(s, a.Id);
        Assert.True(answer.NeedsReview);
        Assert.Contains("Line 2: the sign before 3 is unclear. 1 mark depends on it.", answer.ReviewNote);
        var issue = Assert.Single(AiCostReport.Verdict(answer)!.TranscriptionIssues);
        Assert.Equal((2, "-3", "3"), (issue.Step!.Value, issue.OnPhoto, issue.Transcribed));
    }

    [Fact]
    public async Task A_right_method_with_a_slip_at_the_end_keeps_its_method_marks()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: ... x = 3");
        _ai.Reply = r => r.Kind switch
        {
            "recognition"  => Recognised("2x + 6 = 10", "2x = 4", "x = 3"),
            "verification" => """{"overall":"incorrect","agrees":true,"confidence":0.9,"notes":"","steps":[]}""",
            _ => """
                {"criteria":[{"criterion":"M1 expands","maxMarks":1,"awarded":1,"explanation":"Correct.","step":1},
                             {"criterion":"M1 isolates x","maxMarks":1,"awarded":1,"explanation":"Correct.","step":2},
                             {"criterion":"A1 x = 2","maxMarks":1,"awarded":0,"explanation":"4 ÷ 2 is 2, not 3.","evidence":"line 3: x = 3","step":3}],
                 "awardedMarks":2,"steps":[{"number":1,"status":"correct"},{"number":2,"status":"correct"},{"number":3,"status":"incorrect","explanation":"4 ÷ 2 = 2."}],
                 "overall":"partial","summary":"Good method; check the last division.","transcriptionIssues":[],"needsTeacher":false}
                """
        };

        var outcome = await EvaluateAsync(s, a.Id, StepsJson("2x + 6 = 10", "2x = 4", "x = 3"), photo);

        Assert.True(outcome.Success);
        Assert.Equal(("partial", 2m, 3m), (outcome.Overall, outcome.Marks!.Value, outcome.MaxMarks!.Value));
        var answer = await AnswerAsync(s, a.Id);
        Assert.Equal((2m, false), (answer.PointsAwarded!.Value, answer.IsCorrect!.Value));   // not 0, not all or nothing
        Assert.False(answer.NeedsReview);
        Assert.Equal("4 ÷ 2 is 2, not 3.", AiCostReport.Verdict(answer)!.Criteria[2].Explanation);
    }

    // ── Never paid twice ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_photo_is_read_once_a_new_photo_is_read_again()
    {
        await SetupAsync();
        await AnalyzeAsync("PAGE A");
        var again = await AnalyzeAsync("PAGE A");                        // reload / second click
        Assert.True(again.FromCache);
        await AnalyzeAsync("PAGE B");                                    // a new photo

        Assert.Equal(2, _ai.All.Count(c => c.Kind == "recognition"));
    }

    [Fact]
    public async Task Two_clicks_at_the_same_time_make_one_call()
    {
        await SetupAsync();
        _ai.Delay = TimeSpan.FromMilliseconds(300);
        var both = await Task.WhenAll(AnalyzeAsync("SAME PAGE"), AnalyzeAsync("SAME PAGE"));

        Assert.Single(_ai.All);
        Assert.Equal(both[0].Key, both[1].Key);
    }

    [Fact]
    public async Task A_failed_marking_keeps_the_work_never_gives_0_and_a_retry_does_not_read_the_photo_again()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: solution");
        var steps = StepsJson("2x + 6 = 10", "2x = 4", "x = 2");
        _ai.Fail = r => r.Kind == "grading" ? HttpStatusCode.ServiceUnavailable : null;

        var failed = await EvaluateAsync(s, a.Id, steps, photo);

        Assert.False(failed.Success);
        Assert.True(failed.CanRetry);
        Assert.Contains("Your work is saved", failed.Error);
        var kept = await AnswerAsync(s, a.Id);
        Assert.Equal(photo, kept.ImagePath);
        Assert.Null(kept.PointsAwarded);                                 // "not marked yet", not 0
        Assert.Null(kept.IsCorrect);
        Assert.True(kept.NeedsReview);
        Assert.True(GradingService.IsUnfinished(kept));
        Assert.NotNull(kept.RecognitionKey);

        // The service is back: the student presses Try again (or the teacher "Retry AI marking").
        _ai.Fail = _ => null;
        var retried = await EvaluateAsync(s, a.Id, steps, photo);
        Assert.True(retried.Success);
        Assert.Equal(1, _ai.All.Count(c => c.Kind == "recognition"));    // read once, in all
        Assert.Equal(2, _ai.All.Count(c => c.Kind == "grading"));        // the failed try and the retry

        // Pressing it again, or reopening the page, costs nothing more.
        var before = _ai.All.Count;
        Assert.True((await EvaluateAsync(s, a.Id, steps, photo)).Success);
        Assert.Equal(before, _ai.All.Count);
        var answer = await AnswerAsync(s, a.Id);
        Assert.Equal(3m, answer.PointsAwarded);
        Assert.False(GradingService.IsUnfinished(answer));

        // Every call is on record with its stage, model, tokens and cost; nothing of the work itself.
        var logs = await _db.NewContext().AiCallLogs.OrderBy(l => l.CreatedAt).ToListAsync();
        Assert.Equal(new[] { "recognition", "grading", "grading", "verification" }, logs.Select(l => l.Stage));
        Assert.Equal(new[] { Gemini, Gpt, Gpt, Gpt }, logs.Select(l => l.Model));
        Assert.Equal("unavailable", logs[1].Failure);
        Assert.All(logs.Where(l => l.Succeeded), l => Assert.Equal((1200, 300, 0.0125m), (l.InputTokens!.Value, l.OutputTokens!.Value, l.Cost!.Value)));
        var cost = (await AiCostReport.ForResultAsync(_db.NewContext(), (await AnswerAsync(s, a.Id)).StudentResult))[0];
        Assert.Equal(4, cost.Calls);
        Assert.Equal(0.0375m, cost.Cost);
    }

    [Fact]
    public async Task A_changed_mark_scheme_marks_again_without_reading_the_photo_again()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: solution");
        var steps = StepsJson("2x + 6 = 10", "2x = 4", "x = 2");
        await EvaluateAsync(s, a.Id, steps, photo);

        await using (var db = _db.NewContext())
        {
            var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
            row.Questions = row.Questions.Replace("A1 x = 2", "A1 x = 2 with a check");
            await db.SaveChangesAsync();
        }
        await EvaluateAsync(s, a.Id, steps, photo);

        Assert.Equal(1, _ai.All.Count(c => c.Kind == "recognition"));
        Assert.Equal(2, _ai.All.Count(c => c.Kind == "grading"));
        Assert.Contains("with a check", _ai.All.Last(c => c.Kind == "grading").Text);
    }

    [Fact]
    public async Task Work_handed_in_while_marking_was_unfinished_is_finished_after_the_hand_in()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: solution");
        _ai.Fail = r => r.Kind == "grading" ? HttpStatusCode.GatewayTimeout : null;
        await EvaluateAsync(s, a.Id, StepsJson("2x + 6 = 10", "2x = 4", "x = 2"), photo);

        _ai.Fail = _ => null;
        await using (var db = _db.NewContext())
        {
            var submissions = new SubmissionService(db, AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env), _env, NullLogger<SubmissionService>.Instance);
            var saved = await submissions.SubmitAsync(s, a.Id, Guid.NewGuid(), new Dictionary<string, string> { ["q_1"] = "B" });
            Assert.True(saved.GradingPending);                           // the photo still needs its marking
            await submissions.GradeAfterSubmitAsync(saved.ResultId);
        }

        var answer = await AnswerAsync(s, a.Id);
        Assert.Equal(3m, answer.PointsAwarded);
        Assert.Equal(1, _ai.All.Count(c => c.Kind == "recognition"));
    }

    [Fact]
    public async Task A_drawing_is_marked_by_the_vision_model_and_waits_for_the_teacher()
    {
        var (s, a) = await SetupAsync();
        await using (var db = _db.NewContext())
        {
            var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
            row.Questions = """[{"id":"d0","title":"Sketch y = x","type":"3","answerKey":"","requireDrawing":true}]""";
            await db.SaveChangesAsync();
        }
        _ai.Reply = _ => """{"correct":true,"marks":1,"confidence":0.8,"feedback":"Good line."}""";
        const string png = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

        await using (var db = _db.NewContext())
        {
            var submissions = new SubmissionService(db, AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env), _env, NullLogger<SubmissionService>.Instance);
            var saved = await submissions.SubmitAsync(s, a.Id, Guid.NewGuid(), new Dictionary<string, string> { ["sketchpng_0"] = png });
            Assert.True(saved.GradingPending);
            await submissions.GradeAfterSubmitAsync(saved.ResultId);
        }

        var call = Assert.Single(_ai.All);
        Assert.Single(call.Images);
        var answer = await AnswerAsync(s, a.Id);
        Assert.True(answer.NeedsReview);                                  // a drawing always waits for the teacher
        Assert.Equal(1m, answer.PointsAwarded);
    }

    [Fact]
    public async Task A_draft_saved_while_the_photo_is_being_marked_does_not_lose_the_marking()
    {
        var (s, a) = await SetupAsync();
        var photo = await PhotoAsync(s, "PHOTO: solution");
        _ai.Delay = TimeSpan.FromMilliseconds(400);                     // the AI takes a moment…

        var marking = EvaluateAsync(s, a.Id, StepsJson("2x + 6 = 10", "2x = 4", "x = 2"), photo);
        await Task.Delay(100);
        StudentResult drafted;                                           // …meanwhile the page autosaves a typed answer
        await using (var db = _db.NewContext())
            drafted = await AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env).GetOrCreateSavedResultAsync(a.Id, s);
        var outcome = await marking;

        Assert.True(outcome.Success, outcome.Error);
        await using var check = _db.NewContext();
        var attempts = await check.StudentResults.Where(r => r.StudentId == s && r.AssignmentId == a.Id).ToListAsync();
        Assert.Equal(drafted.Id, Assert.Single(attempts).Id);           // one attempt, shared
        Assert.Equal(3m, (await AnswerAsync(s, a.Id)).PointsAwarded);
    }

    [Fact]
    public async Task Working_autosaved_for_the_same_question_while_it_is_marked_does_not_lose_the_marking()
    {
        var (s, a) = await SetupAsync();
        _ai.Delay = TimeSpan.FromMilliseconds(400);
        await using (var db = _db.NewContext())                         // the attempt exists (the student typed before)
            await AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env).GetOrCreateSavedResultAsync(a.Id, s);

        var marking = EvaluateAsync(s, a.Id, StepsJson("2x + 6 = 10", "2x = 4", "x = 2"), "");   // typed steps for question 1
        await Task.Delay(100);
        await using (var db = _db.NewContext())                         // meanwhile the draft autosave writes that question's working
        {
            var r = await db.StudentResults.FirstAsync(x => x.StudentId == s && x.AssignmentId == a.Id && !x.IsSubmitted);
            db.StudentAnswers.Add(new StudentAnswer { StudentResultId = r.Id, QuestionIndex = 0, QuestionId = "q0", WorkText = "2x + 6 = 10" });
            await db.SaveChangesAsync();
        }
        var outcome = await marking;

        Assert.True(outcome.Success, outcome.Error);
        Assert.Equal(3m, (await AnswerAsync(s, a.Id)).PointsAwarded);
    }

    // ── Older work ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Older_work_and_teachers_marks_still_open_and_are_never_re_marked()
    {
        var (s, a) = await SetupAsync();
        Guid resultId;
        await using (var db = _db.NewContext())
        {
            var r = new StudentResult { AssignmentId = a.Id, StudentId = s, AttemptNumber = 1, IsSubmitted = true, Score = 67 };
            r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 0, QuestionId = "q0", ImagePath = "/uploads/submissions/old.jpg",
                                                     AnswerValue = "x = 2", IsCorrect = true, PointsAwarded = 3, AiSummary = "old" });
            r.StudentAnswers.Add(new StudentAnswer { QuestionIndex = 2, QuestionId = "q2", AnswerValue = "because", IsCorrect = false,
                                                     PointsAwarded = 1, TeacherOverridden = true, TeacherComment = "half right" });
            r.StepResults.Add(new StepResult { QuestionIndex = 0, StepNumber = 1, StepText = "x = 2", Status = ResultStatus.Correct });
            db.StudentResults.Add(r);
            await db.SaveChangesAsync();
            resultId = r.Id;
        }

        await using (var db = _db.NewContext())
        {
            var r = await db.StudentResults.Include(x => x.StudentAnswers).Include(x => x.Assignment).FirstAsync(x => x.Id == resultId);
            Assert.All(r.StudentAnswers, x => Assert.Null(AiCostReport.Verdict(x)));
            Assert.All(r.StudentAnswers, x => Assert.False(GradingService.IsUnfinished(x)));
            Assert.Empty(await AiCostReport.ForResultAsync(db, r));
            var submissions = new SubmissionService(db, AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env), _env, NullLogger<SubmissionService>.Instance);
            Assert.False(submissions.NeedsAi(r, JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>(r.Assignment.Questions)!));
        }

        // A teacher's own mark stands even when the AI marks the answer again.
        await using (var db = _db.NewContext())
            await AiTestServices.Grading(db, _db.NewContext, _config, _ai, _env).RegradeAnswerAsync(resultId, 0);
        await using (var db = _db.NewContext())
        {
            var teacherMarked = await db.StudentAnswers.SingleAsync(x => x.StudentResultId == resultId && x.QuestionIndex == 2);
            Assert.Equal((1m, true, "half right"), (teacherMarked.PointsAwarded!.Value, teacherMarked.TeacherOverridden, teacherMarked.TeacherComment));
        }
        Assert.Empty(_ai.All.Where(c => c.Kind == "typed"));
    }
}
