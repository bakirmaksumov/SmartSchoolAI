using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// AI-grades typed answers that cannot be graded deterministically:
/// open/short answers with no answer key, or text answers that didn't
/// exactly match the key (accepts equivalent forms like "x = 2" vs "2").
/// All items are graded in a single model call to keep cost down.
/// </summary>
public class AnswerGradingService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AnswerGradingService> _logger;

    public AnswerGradingService(
        IConfiguration config, IHttpClientFactory httpFactory, ILogger<AnswerGradingService> logger)
    {
        _config      = config;
        _httpFactory = httpFactory;
        _logger      = logger;
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
        Return ONLY valid JSON — no markdown fences, no explanation:
        [ { "index": 1, "correct": true, "feedback": "<one short sentence for the student>" } ]
        """;

    /// <summary>One typed answer to grade. Index is the 0-based question index.</summary>
    public sealed record ItemToGrade(int Index, string Question, string StudentAnswer, string? ExpectedAnswer);

    /// <summary>The model's verdict for one item.</summary>
    public sealed class GradedItem
    {
        public int    Index    { get; set; }
        public bool   Correct  { get; set; }
        public string Feedback { get; set; } = string.Empty;
    }

    /// <summary>Grades all items in one model call. Returns verdicts keyed by question index.</summary>
    public async Task<Dictionary<int, GradedItem>> GradeAsync(IReadOnlyList<ItemToGrade> items)
    {
        var apiKey  = _config["OpenRouter:ApiKey"]  ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        var model   = _config["OpenRouter:Model"]   ?? "openai/gpt-4o";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenRouter API key is not configured. Add 'OpenRouter:ApiKey' to appsettings.");

        var sb = new StringBuilder("Grade the following answers.\n\n");
        foreach (var item in items)
        {
            sb.AppendLine($"Item {item.Index}:");
            sb.AppendLine($"Question: {item.Question}");
            sb.AppendLine($"Expected answer: {(string.IsNullOrWhiteSpace(item.ExpectedAnswer) ? "(not provided)" : item.ExpectedAnswer)}");
            sb.AppendLine($"Student's answer: {item.StudentAnswer}");
            sb.AppendLine();
        }

        var payload = new
        {
            model,
            temperature = 0.1,   // grading should be as deterministic as possible
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = sb.ToString() }
            }
        };

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(3);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        _logger.LogInformation("Grading {Count} typed answer(s) via OpenRouter …", items.Count);

        var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter returned {Status}: {Body}", response.StatusCode, body);
            throw new Exception($"AI grading returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var raw = doc.RootElement
                     .GetProperty("choices")[0]
                     .GetProperty("message")
                     .GetProperty("content")
                     .GetString() ?? "[]";

        // Strip markdown fences if the model wrapped the JSON
        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
        if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
        raw = raw.Trim();

        // Repair LaTeX single backslashes so the JSON is valid.
        raw = JsonSanitizer.FixInvalidBackslashes(raw);

        var graded = JsonSerializer.Deserialize<List<GradedItem>>(raw,
                         new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                     ?? new List<GradedItem>();

        return graded
            .GroupBy(g => g.Index)
            .ToDictionary(g => g.Key, g => g.First());
    }
}
