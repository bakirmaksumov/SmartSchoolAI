using System.Text;
using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// AI-grades typed answers that cannot be graded deterministically: open/short answers with no key, or text answers
/// that did not exactly match the key (equivalent forms like "x = 2" and "2" are accepted). Sent straight to the
/// grading model (OpenRouter:Grading:GradingModel) with the question, the key and the teacher's mark scheme; no
/// photo-reading call is involved. All items of one submission go in a single call to keep cost down; the call's
/// tokens and cost are recorded for the submission and its questions.
/// </summary>
public class AnswerGradingService
{
    private readonly OpenRouterClient _ai;
    private readonly GradingModels _models;
    private readonly ILogger<AnswerGradingService> _logger;

    public AnswerGradingService(OpenRouterClient ai, GradingModels models, ILogger<AnswerGradingService> logger)
    {
        _ai     = ai;
        _models = models;
        _logger = logger;
    }

    private const string SystemPrompt = """
        You are an expert teacher grading students' typed answers.
        For every numbered item you receive the question, the student's answer and,
        when available, the expected answer.
        - Judge mathematical/factual correctness, not wording.
        - Accept equivalent forms: "x = 2" equals "2", "0.5" equals "1/2", etc.
        - When no expected answer is given, solve the question yourself and compare.
        - Mark "correct": true when the answer is right; only mark it wrong when it is
          clearly incorrect — do not invent mistakes.
        - Watch boundary conditions: an even root's radicand is ≥ 0 (may be zero), a
          denominator must be ≠ 0. Include the boundary unless it breaks the denominator.
        - An answer like "I don't know" or an unrelated statement is incorrect.
        - For a multiple-choice item you receive the choices and the option(s) the student
          selected. Grade only whether the selected statement(s) are true; there is no working to judge.
        - Test special cases before deciding: a parameter equal to zero, a quadratic that becomes
          linear, the endpoints of an interval. A statement that holds in every case is true.
        - Write the feedback in simple, clear English — many students are still
          learning English. Use short sentences and avoid idioms and slang.
        - Every item says how many marks it is worth. Give "marks" as a whole number from 0
          to that number, the way an exam mark scheme would: method marks for a correct
          method even when the final answer is wrong, accuracy marks only for correct
          results. "correct" is true only when the answer earns full marks.
        - When the item has a teacher's mark scheme, follow it: one criterion per mark point (e.g. M1 for the method,
          A1 for the result), each with the marks awarded and a short reason. Without a scheme, still list the
          criteria you used. "marks" is their total.
        Return ONLY valid JSON — no markdown fences, no explanation:
        [ { "index": 1, "correct": true, "marks": 1, "feedback": "<one short sentence for the student>",
            "criteria": [ { "criterion": "A1 correct answer", "maxMarks": 1, "awarded": 1, "explanation": "<why>", "evidence": "<the part of the answer>" } ] } ]
        """;

    /// <summary>One typed answer to grade. Index is the 0-based question index.</summary>
    public sealed record ItemToGrade(int Index, string Question, string StudentAnswer, string? ExpectedAnswer, decimal MaxMarks = 1, string? MarkScheme = null);

    /// <summary>The model's verdict for one item.</summary>
    public sealed class GradedItem
    {
        public int    Index    { get; set; }
        public bool   Correct  { get; set; }

        /// <summary>Marks the model awarded, out of the item's marks; null when it gave none.</summary>
        [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
        public decimal? Marks { get; set; }

        public string Feedback { get; set; } = string.Empty;

        /// <summary>Marks per criterion with the reason; empty when the model gave none.</summary>
        public List<AiMarkingService.Criterion> Criteria { get; set; } = new();
    }

    /// <summary>Grades all items in one model call. Returns verdicts keyed by question index.</summary>
    public async Task<Dictionary<int, GradedItem>> GradeAsync(IReadOnlyList<ItemToGrade> items, AiCallContext? context = null)
    {
        var sb = new StringBuilder("Grade the following answers.\n\n");
        foreach (var item in items)
        {
            sb.AppendLine($"Item {item.Index}:");
            sb.AppendLine($"Question: {item.Question}");
            sb.AppendLine($"Marks available: {item.MaxMarks:0.##}");
            sb.AppendLine($"Expected answer: {(string.IsNullOrWhiteSpace(item.ExpectedAnswer) ? "(not provided)" : item.ExpectedAnswer)}");
            if (!string.IsNullOrWhiteSpace(item.MarkScheme)) sb.AppendLine($"Teacher's mark scheme: {item.MarkScheme}");
            sb.AppendLine($"Student's answer: {item.StudentAnswer}");
            sb.AppendLine();
        }

        var reply = await _ai.CompleteAsync(_models.Grading, new object[]
        {
            new { role = "system", content = SystemPrompt },
            new { role = "user",   content = sb.ToString() }
        }, (context ?? new AiCallContext("typed-grading")) with { Questions = string.Join(",", items.Select(i => i.Index)) },
           temperature: _models.GradingTemperature, timeout: _models.Timeout);

        List<GradedItem> graded;
        try
        {
            graded = JsonSerializer.Deserialize<List<GradedItem>>(OpenRouterClient.CleanJson(reply.Content),
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
        catch (JsonException ex)
        {
            throw new AiCallException("bad-response", "The AI's marking reply could not be read.", ex);
        }

        _logger.LogInformation("Typed grading: {Count} item(s), {Graded} verdict(s)", items.Count, graded.Count);
        return graded.GroupBy(g => g.Index).ToDictionary(g => g.Key, g => g.First());
    }
}
