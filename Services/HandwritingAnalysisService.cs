using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.Services;

/// <summary>
/// Analyses a photo of a student's handwritten solution in two passes:
///   1. <see cref="ExtractStepsAsync"/> — OCR only: reads each written step,
///      no grading (the student reviews/corrects the transcription first).
///   2. <see cref="EvaluateStepsAsync"/> — grades the corrected steps against
///      the question, returning a per-step verdict and an overall summary.
/// OCR uses a vision model (Qwen-VL); evaluation uses the configured text model.
/// </summary>
public class HandwritingAnalysisService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HandwritingAnalysisService> _logger;

    public HandwritingAnalysisService(
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<HandwritingAnalysisService> logger)
    {
        _config      = config;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    private const string OcrSystemPrompt = """
        You are an OCR engine for handwritten math.
        Read the image and transcribe every line the student wrote, in order.
        Do NOT judge correctness and do NOT solve the problem.

        CROSSED-OUT TEXT: if a line, word or step is struck through, scribbled
        over, or otherwise marked as rejected by the student, LEAVE IT OUT of
        the transcription entirely — it is an abandoned attempt, not a step in
        the student's actual solution. Only transcribe the work the student
        left standing. Including an abandoned attempt between two real steps
        makes a correct solution look self-contradictory to whoever grades it
        next.

        AMBIGUOUS CHARACTERS: a handwritten "?" is a question mark, not a
        digit — students write "x = ?" to mark a value they have not yet
        found. Transcribe it literally as "?", never as a digit it happens to
        resemble (a "?" has been misread as "9" before, silently turning an
        unsolved step into a fabricated answer). The same care applies to any
        other genuinely ambiguous digit or symbol: when unsure, transcribe
        your best reading rather than guessing confidently — do not silently
        resolve an ambiguity you are not sure about.

        Formatting:
        - Wrap every mathematical expression in $...$ so it renders, e.g.
          "x > $-\frac{2a}{3}$" and "$y = |x + 3a|$".
        - Use $$...$$ for a multi-line construct such as \begin{cases}...\end{cases}.
        - Plain words stay outside the $ delimiters.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "detectedProblem": "<the problem if visible, else empty>",
          "steps": [ { "number": 1, "text": "<exact line as written>" } ]
        }
        """;

    private const string EvalSystemPrompt = """
        You are an expert math teacher grading a student's transcribed solution.

        First, solve the task yourself so you know the correct final answer and, for
        domain/inequality problems, the correct boundary conditions. Then check the
        student's steps against your own work.

        Grading rules:
        - Mark a step "incorrect" ONLY when it is clearly mathematically wrong. If every
          step is valid, mark them all "correct" — never invent an error.
        - Judge mathematical correctness, not wording, notation or formatting. Accept
          equivalent forms (e.g. "x = 2" and "2", "1/2" and "0.5").
        - Domain rules: an even root's radicand must be ≥ 0 (it MAY equal zero); a
          denominator must be ≠ 0, so a radicand under a root in the denominator must be
          > 0 (strict). Include the boundary (≥) unless it makes a denominator zero.
        - "overall" is "correct" when the student's final result is correct, even if a
          step is phrased loosely.
        - Write explanations and the summary in simple, clear English — many students
          are still learning English. Use short sentences and avoid idioms and slang.
        - Keep each step's text exactly as given, including its $...$ math delimiters,
          and wrap any maths you add in $...$ too so it renders.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "steps": [ { "number": 1, "text": "<step text>", "status": "correct|incorrect", "explanation": "<why, only if incorrect>" } ],
          "overall": "correct|incorrect",
          "expectedAnswer": "<the correct final answer>",
          "summary": "<one sentence of feedback>"
        }
        """;

    // ── 1. OCR pass (vision) ─────────────────────────────────────────────────
    // Accepts one or more photos — a student may spread a solution across several
    // sheets. All pages go into a single vision request so the steps are read in order.
    public async Task<HandwritingAnalysisDto> ExtractStepsAsync(
        IReadOnlyList<(byte[] bytes, string mime)> images, string questionText)
    {
        var (apiKey, baseUrl, _) = GetConfig();

        var subject = images.Count > 1
            ? $"{images.Count} pages of one handwritten solution, in order,"
            : "the handwritten solution";

        var userText = string.IsNullOrWhiteSpace(questionText)
            ? $"These image(s) show {subject}. Transcribe every written line, in order, across all pages."
            : $"The assigned task is:\n'{questionText}'\n\nThese image(s) show {subject}. " +
              "Transcribe every written line, in order, across all pages.";

        // One image_url block per page, then the instruction text.
        var content = new List<object>();
        foreach (var (bytes, mime) in images)
        {
            var dataUri = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            content.Add(new { type = "image_url", image_url = new { url = dataUri, detail = "high" } });
        }
        content.Add(new { type = "text", text = userText });

        var payload = new Dictionary<string, object>
        {
            ["model"]       = GetVisionModel(),
            ["temperature"] = 0.2,
            ["messages"]    = new object[]
            {
                new { role = "system", content = OcrSystemPrompt },
                new { role = "user",   content = content.ToArray() }
            }
        };

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── 2. Evaluation pass (text, or text + reference-solution image) ────────
    public async Task<HandwritingAnalysisDto> EvaluateStepsAsync(
        string questionText, string? answerKey, IEnumerable<HandwritingStepDto> steps,
        (byte[] bytes, string mime)? answerKeyImage = null)
    {
        var (apiKey, baseUrl, model) = GetConfig();

        var stepLines = string.Join("\n",
            steps.Select(s => $"{s.Number}. {s.Text}"));

        var userText =
            $"Task:\n{questionText}\n\n" +
            $"Expected answer: {(string.IsNullOrWhiteSpace(answerKey) ? "(not provided)" : answerKey)}\n\n" +
            $"Student's steps:\n{stepLines}";

        object payload;
        if (answerKeyImage is { } img)
        {
            userText += "\n\nThe attached image is the teacher's reference solution — " +
                        "compare the student's steps against it visually as well as textually.";

            var dataUri = $"data:{img.mime};base64,{Convert.ToBase64String(img.bytes)}";
            var content = new object[]
            {
                new { type = "image_url", image_url = new { url = dataUri, detail = "high" } },
                new { type = "text", text = userText }
            };

            payload = new
            {
                model       = GetVisionModel(),
                temperature = 0.1,
                messages = new object[]
                {
                    new { role = "system", content = EvalSystemPrompt },
                    new { role = "user",   content }
                }
            };
        }
        else
        {
            payload = new
            {
                model,
                temperature = 0.1,   // grading should be as deterministic as possible
                messages = new object[]
                {
                    new { role = "system", content = EvalSystemPrompt },
                    new { role = "user",   content = userText }
                }
            };
        }

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── 3. Verification pass (second opinion on the grade) ───────────────────

    private const string VerifySystemPrompt = """
        You are a rigorous, sceptical exam moderator. You are given a task, a
        student's steps, and a grade another marker already gave.

        Do this in order:
        1. Solve the task yourself, independently, without trusting the grade.
        2. Where possible, confirm your answer by a second method (substitution,
           checking a boundary value, or an alternative approach).
        3. Compare your conclusion with the grade you were given.

        Be especially careful with the mistakes markers commonly make:
        - Boundary conditions: an even root's radicand may be ZERO (use ≥ 0), while a
          denominator must be non-zero (strict > 0). Do not turn ≥ into > by accident.
        - Equivalent forms written differently ("x = 2" and "2", "1/2" and "0.5").
        - Marking a valid but unusual method as wrong.
        - Sign errors and dropped terms.

        Report your own verdict, whether you agree with the marker, and how confident
        you are. Use a LOW confidence when the task is ambiguous, the steps are unclear
        or you cannot verify the result. Write notes in simple, clear English.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "overall": "correct|incorrect",
          "agrees": true|false,
          "confidence": 0.0,
          "notes": "<one or two short sentences for the teacher>"
        }
        """;

    /// <summary>
    /// Independently re-checks a grade. Returns null when verification is disabled
    /// or the call fails — callers then keep the original grade unchanged.
    /// </summary>
    public async Task<GradingVerificationDto?> VerifyEvaluationAsync(
        string questionText, string? answerKey,
        IEnumerable<HandwritingStepDto> steps, HandwritingAnalysisDto evaluation)
    {
        if (!_config.GetValue("OpenRouter:EnableGradingVerification", true))
            return null;

        var (apiKey, baseUrl, model) = GetConfig();

        var stepLines = string.Join("\n", steps.Select(s => $"{s.Number}. {s.Text}"));

        var userText =
            $"Task:\n{questionText}\n\n" +
            $"Expected answer: {(string.IsNullOrWhiteSpace(answerKey) ? "(not provided)" : answerKey)}\n\n" +
            $"Student's steps:\n{stepLines}\n\n" +
            $"The marker's grade was: {evaluation.Overall}\n" +
            $"The marker's reason: {evaluation.Summary}";

        var payload = new
        {
            model,
            temperature = 0.1,
            messages = new object[]
            {
                new { role = "system", content = VerifySystemPrompt },
                new { role = "user",   content = userText }
            }
        };

        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            var content  = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
            var body     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Verification returned {Status}", response.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var raw = doc.RootElement.GetProperty("choices")[0]
                         .GetProperty("message").GetProperty("content").GetString() ?? "{}";

            raw = raw.Trim();
            if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
            if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
            raw = JsonSanitizer.FixInvalidBackslashes(raw.Trim());

            return JsonSerializer.Deserialize<GradingVerificationDto>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            // Verification is a safety net — never block grading when it fails.
            _logger.LogWarning(ex, "Grading verification failed; keeping the original grade.");
            return null;
        }
    }

    // ── Shared HTTP call ─────────────────────────────────────────────────────
    private async Task<HandwritingAnalysisDto> CallOpenRouterAsync(
        string apiKey, string baseUrl, object payload)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling OpenRouter ({BaseUrl}) for handwriting analysis …", baseUrl);

        var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter returned {Status}: {Body}", response.StatusCode, body);
            var errorMsg = TryExtractErrorMessage(body);
            if (string.IsNullOrEmpty(errorMsg)) errorMsg = body.Length > 500 ? body[..500] : body;
            throw new Exception($"AI service returned {(int)response.StatusCode}. {errorMsg}");
        }

        using var doc = JsonDocument.Parse(body);
        var raw = doc.RootElement
                     .GetProperty("choices")[0]
                     .GetProperty("message")
                     .GetProperty("content")
                     .GetString() ?? "{}";

        // Strip markdown fences if the model wrapped the JSON
        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
        if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
        raw = raw.Trim();

        // Repair LaTeX single backslashes so the JSON is valid.
        raw = JsonSanitizer.FixInvalidBackslashes(raw);

        try
        {
            return JsonSerializer.Deserialize<HandwritingAnalysisDto>(raw,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new HandwritingAnalysisDto();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to deserialise handwriting response. Raw: {Raw}", raw);
            return new HandwritingAnalysisDto();
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private (string apiKey, string baseUrl, string model) GetConfig()
    {
        var apiKey  = _config["OpenRouter:ApiKey"]  ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        var model   = _config["OpenRouter:Model"]   ?? "openai/gpt-4o";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenRouter API key is not configured. Add 'OpenRouter:ApiKey' to appsettings.");

        return (apiKey, baseUrl, model);
    }

    private string GetVisionModel()
        => _config["OpenRouter:VisionModel"] ?? "qwen/qwen2.5-vl-72b-instruct";

    private static string TryExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return err.TryGetProperty("message", out var msg)
                    ? msg.GetString() ?? "" : err.ToString();
        }
        catch { }
        return "";
    }
}
