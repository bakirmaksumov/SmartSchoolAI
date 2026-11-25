using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// A real, paid run against OpenRouter with the configured models — OFF by default. It runs only when
/// SMARTSCHOOL_REAL_AI=1 and OPENROUTER_API_KEY are set, and prints what each model returned and what it cost.
/// It uses two sample photos of handwritten work from the E2E fixtures. It checks that the models answer and the
/// pipeline holds together; it says nothing about accuracy on real classwork.
/// </summary>
public class RealOpenRouterSmokeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly FileDb _db = new();
    private readonly FakeWebHostEnvironment _env = new();

    public RealOpenRouterSmokeTests(ITestOutputHelper output) => _out = output;
    public void Dispose() { _db.Dispose(); _env.Dispose(); }

    private sealed class RealHttp : IHttpClientFactory { public HttpClient CreateClient(string name) => new(); }

    [Fact]
    public async Task Real_models_read_and_mark_two_sample_photos()
    {
        var key = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (Environment.GetEnvironmentVariable("SMARTSCHOOL_REAL_AI") != "1" || string.IsNullOrWhiteSpace(key)) return;

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenRouter:ApiKey"] = key,
            ["OpenRouter:BaseUrl"] = "https://openrouter.ai/api/v1",
            ["OpenRouter:EnableGradingVerification"] = "true",
            ["OpenRouter:Grading:RecognitionModel"] = "google/gemini-3.1-pro-preview",
            ["OpenRouter:Grading:GradingModel"] = "openai/gpt-5.5",
            ["OpenRouter:Grading:TimeoutSeconds"] = "300"
        }).Build();

        var t = await _db.UserAsync(Role.Teacher, "T");
        var s = await _db.UserAsync(Role.Student, "S");
        var cls = await _db.ClassAsync(t.Id, s.Id);
        var a = await _db.AssignmentAsync(cls.Id, t.Id, status: AssignmentStatus.Active);
        await using (var db = _db.NewContext())
        {
            var row = await db.Assignments.FirstAsync(x => x.Id == a.Id);
            row.Questions = """
                [{"id":"q0","title":"Evaluate $2^{-3}$. Show your working.","type":"0","answerKey":"1/8","marks":2,
                  "markScheme":"M1 uses a^{-n} = 1/a^n (or an equivalent first step); A1 1/8"}]
                """;
            row.MaxAttempts = 0;
            db.AssignmentStudents.Add(new AssignmentStudent { AssignmentId = a.Id, StudentId = s.Id });
            await db.SaveChangesAsync();
        }

        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "E2E", "fixtures"));
        foreach (var file in new[] { "solution-correct.jpg", "solution-wrong.jpg" })
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(fixtures, file));
            var web = await SubmissionStorage.SaveAsync(_env, bytes, ".jpg", s.Id);
            var http = new RealHttp();

            // 1. What the student's dialog does: read the photo.
            var marking = AiTestServices.Marking(_db.NewContext, config, http);
            var reading = await marking.RecognizeAsync(new[] { new AiMarkingService.Page(bytes, "image/jpeg") },
                "Evaluate $2^{-3}$. Show your working.", new AiCallContext("recognition", null, s.Id, a.Id));
            _out.WriteLine($"=== {file}: read by {reading.Model}");
            foreach (var st in reading.Steps)
                _out.WriteLine($"  {st.Number}. {st.Text}" + (st.Unclear.Count > 0 ? $"   [unclear: {string.Join("; ", st.Unclear.Select(u => u.Fragment))}]" : ""));

            // 2. The student confirms the steps unchanged; marking runs (the reading is reused, not paid again).
            var steps = JsonSerializer.Serialize(reading.Steps.Select(x => new { number = x.Number, text = x.Text }));
            GradingService.StepsGradeOutcome outcome;
            await using (var db = _db.NewContext())
                outcome = await AiTestServices.Grading(db, _db.NewContext, config, http, _env)
                    .EvaluateStepsAsync(a.Id, 0, s.Id, steps, JsonSerializer.Serialize(new[] { web }));
            _out.WriteLine($"  outcome: success={outcome.Success} overall={outcome.Overall} marks={outcome.Marks}/{outcome.MaxMarks} error={outcome.Error}");

            await using (var db = _db.NewContext())
            {
                var answer = await db.StudentAnswers.Include(x => x.StudentResult)
                    .Where(x => x.StudentResult.StudentId == s.Id && x.QuestionIndex == 0)
                    .OrderByDescending(x => x.UpdatedAt).FirstAsync();
                var verdict = AiCostReport.Verdict(answer);
                foreach (var c in verdict?.Criteria ?? new())
                    _out.WriteLine($"  [{c.Name}] {c.Awarded}/{c.MaxMarks} — {c.Explanation} (based on: {c.Evidence})");
                foreach (var i in verdict?.TranscriptionIssues ?? new())
                    _out.WriteLine($"  transcription issue: line {i.Step} read '{i.Transcribed}', photo '{i.OnPhoto}', certain={i.Certain}");
                _out.WriteLine($"  summary: {verdict?.Summary}");
                _out.WriteLine($"  second check: agrees={verdict?.Verification?.Agrees} confidence={verdict?.Verification?.Confidence}");
                _out.WriteLine($"  needs teacher: {answer.NeedsReview} {answer.ReviewNote}");
                // A new attempt for the next photo.
                var r = await db.StudentResults.FirstAsync(x => x.Id == answer.StudentResultId);
                r.IsSubmitted = true;
                await db.SaveChangesAsync();
            }
        }

        var logs = await _db.NewContext().AiCallLogs.OrderBy(l => l.CreatedAt).ToListAsync();
        _out.WriteLine("=== calls");
        foreach (var l in logs)
            _out.WriteLine($"  {l.Stage,-13} {l.Model,-32} ok={l.Succeeded} {l.Failure} in={l.InputTokens} out={l.OutputTokens} cost={(l.Cost is { } c ? $"${c:0.#####}" : "no data")} {l.DurationMs} ms");
        _out.WriteLine($"  total: {logs.Count} calls, ${logs.Sum(l => l.Cost ?? 0):0.#####}");
        Assert.All(logs, l => Assert.True(l.Succeeded, $"{l.Stage} {l.Model}: {l.Failure}"));
    }
}
