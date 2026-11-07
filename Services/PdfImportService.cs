using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSchoolAI.Services;

/// <summary>
/// Extracts text from a PDF file, then asks GPT-4o to parse it into
/// structured question objects ready to be added to an assignment.
/// If text extraction fails (scanned / image-based PDF), the raw PDF
/// bytes are sent as a base64 file to GPT-4o vision via OpenRouter.
/// </summary>
public class PdfImportService
{
    private readonly IConfiguration _config;
    private readonly ILogger<PdfImportService> _logger;

    public PdfImportService(IConfiguration config, ILogger<PdfImportService> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ── 1. Try extracting plain text from PDF using itext7 ───────────────────
    public static string ExtractText(byte[] pdfBytes)
    {
        var sb = new StringBuilder();
        using var ms     = new MemoryStream(pdfBytes);
        using var reader = new PdfReader(ms, new ReaderProperties());
        using var pdf    = new PdfDocument(reader);

        for (int i = 1; i <= pdf.GetNumberOfPages(); i++)
        {
            try
            {
                var strategy = new LocationTextExtractionStrategy();
                var text     = PdfTextExtractor.GetTextFromPage(pdf.GetPage(i), strategy);
                if (!string.IsNullOrWhiteSpace(text))
                    sb.AppendLine(text);
            }
            catch
            {
                // page failed — skip
            }
        }
        return sb.ToString();
    }

    // ── Shared system prompt ─────────────────────────────────────────────────
    private static readonly string SystemPrompt = """
        You are an expert educator assistant. You will be given the content of an exam, test, or worksheet.
        Identify every question and return them as a JSON array.

        Rules:
        - Detect question type automatically:
          * If it has labelled options (A, B, C, D  or  a) b) c) d)) → "MultipleChoice"
          * If it has exactly 2 options (True/False, Yes/No) → "SingleChoice"
          * If it asks for a numeric or short written answer → "ShortAnswer"
          * If it asks for a longer written explanation → "OpenText"
        - Extract all answer options for choice questions.
        - If an answer key is present, extract the correct answer; otherwise leave answerKey blank.
        - Preserve question numbering if present.
        - Write ALL mathematics as LaTeX wrapped in $...$ delimiters, preserving exact
          structure: nth roots as \sqrt[n]{...}, fractions as \frac{a}{b}, powers as x^{2},
          subscripts as x_{1}, integrals as \int, and keep every "=", operator and sign
          exactly as written. Example: "√16 · ⁶√16" → "$\sqrt{16} \cdot \sqrt[6]{16}$".
        - Text extraction cannot carry diagrams, graphs, or figures — only text/LaTeX.
          If a question's wording clearly depends on a diagram you can't see (e.g. "In
          the triangle shown...", "Using the graph below...", a reference to a figure,
          table image, or coordinate plane), set "hasDiagram" to true and "diagramHint"
          to a one-sentence description of what the missing diagram most likely shows,
          inferred from the surrounding text. Otherwise set "hasDiagram" to false and
          "diagramHint" to null.
        - Set "validationWarning" to a short, specific sentence if the question itself
          looks incomplete or wrong as extracted — e.g. no answer could be determined,
          the options are missing or don't match the question, the wording is cut off
          or ambiguous. Otherwise set it to null. Do not invent problems that aren't
          there — most questions should have "validationWarning": null.
        - Return ONLY valid JSON — no markdown fences, no explanation.

        JSON schema (array of objects):
        [
          {
            "number": 1,
            "title": "<full question text>",
            "type": "MultipleChoice|SingleChoice|ShortAnswer|OpenText",
            "options": ["option A text", "option B text", ...],
            "answerKey": "<correct answer or empty string>",
            "difficulty": "Easy|Medium|Hard",
            "hasDiagram": false,
            "diagramHint": null,
            "validationWarning": null
          }
        ]
        """;

    // ── Generation system prompt ─────────────────────────────────────────────
    private static readonly string GenerateSystemPrompt = """
        You are an expert teacher who writes high-quality practice and assessment
        questions. Generate original questions on the requested topic and return
        them as a JSON array only.

        Rules:
        - Produce EXACTLY the requested number of questions.
        - Respect the requested question types. Allowed values:
          "MultipleChoice", "SingleChoice", "ShortAnswer", "OpenText".
          If several types are allowed, vary them sensibly.
        - For MultipleChoice/SingleChoice, always include 3–5 plausible options
          and set answerKey to the FULL TEXT of the correct option.
        - For ShortAnswer, set answerKey to the exact expected answer.
        - For OpenText, leave answerKey as an empty string.
        - Match the requested difficulty and grade level.
        - Write clear, self-contained questions. Write ALL mathematics as LaTeX
          wrapped in $...$ delimiters (\sqrt[n]{...}, \frac{a}{b}, x^{2}, \int, etc.).
        - Set "validationWarning" to a short sentence if you weren't able to make a
          question fully self-contained or unambiguous (rare — you're writing these
          from scratch, so most should have "validationWarning": null).
        - Return ONLY valid JSON — no markdown fences, no explanation.

        JSON schema (array of objects):
        [
          {
            "number": 1,
            "title": "<full question text>",
            "type": "MultipleChoice|SingleChoice|ShortAnswer|OpenText",
            "options": ["option A text", "option B text", ...],
            "answerKey": "<correct answer or empty string>",
            "difficulty": "Easy|Medium|Hard",
            "validationWarning": null
          }
        ]
        """;

    // ── Generate brand-new questions from a topic description ─────────────────
    public async Task<List<ParsedQuestion>> GenerateQuestionsAsync(
        string? subject, string topic, int? gradeLevel,
        string difficulty, int count, string allowedTypes)
    {
        var (apiKey, baseUrl, model) = GetConfig();

        var sb = new StringBuilder();
        sb.AppendLine($"Generate {count} question(s).");
        if (!string.IsNullOrWhiteSpace(subject))    sb.AppendLine($"Subject: {subject}.");
        if (gradeLevel is > 0)                        sb.AppendLine($"Grade level: {gradeLevel}.");
        sb.AppendLine($"Difficulty: {difficulty}.");
        sb.AppendLine($"Allowed question types: {allowedTypes}.");
        sb.AppendLine();
        sb.AppendLine("Topic / instructions:");
        sb.AppendLine(topic);

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = GenerateSystemPrompt },
                new { role = "user",   content = sb.ToString() }
            },
            temperature = 0.7
        };

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── Entry point used by the controllers: pick the cheapest path that works ─
    // The text layer of a PDF can be present yet useless — e.g. an exam whose questions are
    // images or equations, leaving only "1)", "2)" and rows of answer dots. Text extraction then
    // "succeeds" and the model rightly finds nothing, so an empty text result falls back to
    // sending the PDF itself to the vision model.
    public async Task<PdfParseResult> ParseQuestionsFromPdfAsync(byte[] pdfBytes)
    {
        var pdfText    = ExtractText(pdfBytes);
        var meaningful = pdfText.Replace("\n", "").Replace("\r", "").Replace(" ", "").Trim();

        if (meaningful.Length >= 20)
        {
            var fromText = await ParseQuestionsFromTextAsync(pdfText);
            if (fromText.Count > 0)
                return new PdfParseResult(fromText, "text", meaningful.Length);

            _logger.LogInformation("Text path found no questions in {Chars} extracted chars; retrying with the PDF itself.", meaningful.Length);
            return new PdfParseResult(await ParseQuestionsFromImageAsync(pdfBytes), "text-then-image", meaningful.Length);
        }

        return new PdfParseResult(await ParseQuestionsFromImageAsync(pdfBytes), "image", meaningful.Length);
    }

    // ── 2a. Text-based parsing (cheap & fast) ────────────────────────────────
    public async Task<List<ParsedQuestion>> ParseQuestionsFromTextAsync(string pdfText)
    {
        var (apiKey, baseUrl, model) = GetConfig();

        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = $"Parse all questions from this PDF text:\n\n{pdfText}" }
            },
            temperature = 0.2
        };

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── 2b. Image/scanned PDF parsing ──────────────────────────────────────
    // Uses a Gemini model via OpenRouter which natively supports inline PDF
    // files (application/pdf base64), unlike GPT-4o. Model is configurable
    // via OpenRouter:PdfModel so a retired model id can be swapped without a rebuild.
    public async Task<List<ParsedQuestion>> ParseQuestionsFromImageAsync(byte[] pdfBytes)
    {
        var (apiKey, baseUrl, _) = GetConfig();

        // Gemini accepts application/pdf in image_url via OpenRouter
        var base64  = Convert.ToBase64String(pdfBytes);
        var dataUri = $"data:application/pdf;base64,{base64}";

        var payload = new Dictionary<string, object>
        {
            ["model"] = GetPdfModel(),
            ["temperature"] = 0.2,
            ["messages"] = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new
                        {
                            type      = "image_url",
                            image_url = new { url = dataUri }
                        },
                        new { type = "text", text = "Parse all questions from this exam/worksheet PDF." }
                    }
                }
            }
        };

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── 2c. Standalone image parsing (JPG / PNG / WEBP / etc.) ──────────────
    // Sends the raw image bytes as a base64 data URI to GPT-4o vision.
    // Works for photos taken by phone cameras, scanned worksheets, etc.
    public async Task<List<ParsedQuestion>> ParseQuestionsFromImageBytesAsync(
        byte[] imageBytes, string mimeType)
    {
        var (apiKey, baseUrl, _) = GetConfig();

        var base64  = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{mimeType};base64,{base64}";

        var payload = new Dictionary<string, object>
        {
            ["model"]       = GetImageModel(),
            ["temperature"] = 0.2,
            ["messages"]    = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new
                        {
                            type      = "image_url",
                            image_url = new { url = dataUri, detail = "high" }
                        },
                        new { type = "text", text = "Parse all questions from this exam or worksheet image." }
                    }
                }
            }
        };

        return await CallOpenRouterAsync(apiKey, baseUrl, payload);
    }

    // ── Shared HTTP call ─────────────────────────────────────────────────────
    private async Task<List<ParsedQuestion>> CallOpenRouterAsync(
        string apiKey, string baseUrl, object payload)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var json    = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger.LogInformation("Calling OpenRouter ({BaseUrl}) …", baseUrl);

        var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter returned {Status}: {Body}", response.StatusCode, body);
            // Return full error detail to user for debugging
            var errorMsg = TryExtractErrorMessage(body);
            if (string.IsNullOrEmpty(errorMsg)) errorMsg = body.Length > 500 ? body[..500] : body;
            throw new Exception(
                $"AI service returned {(int)response.StatusCode}. {errorMsg}");
        }

        // Parse the response
        using var doc = JsonDocument.Parse(body);
        var raw = doc.RootElement
                     .GetProperty("choices")[0]
                     .GetProperty("message")
                     .GetProperty("content")
                     .GetString() ?? "[]";

        // Strip markdown fences if wrapped
        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
        if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
        raw = raw.Trim();

        // Repair LaTeX single backslashes so the JSON is valid.
        raw = JsonSanitizer.FixInvalidBackslashes(raw);

        _logger.LogInformation("GPT response length: {Len} chars", raw.Length);

        try
        {
            return JsonSerializer.Deserialize<List<ParsedQuestion>>(raw,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new List<ParsedQuestion>();
        }
        catch (Exception ex)
        {
            // Don't hand back an empty list here: that looks like "the file has no questions" and the
            // teacher gets no hint that the AI reply simply couldn't be read.
            _logger.LogWarning(ex, "Failed to deserialise GPT response. Raw: {Raw}", raw);
            throw new InvalidOperationException("The AI reply could not be read. Please try again.", ex);
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

    /// <summary>Model used to parse scanned/image-based PDFs (needs native PDF support).</summary>
    private string GetPdfModel()
        => _config["OpenRouter:PdfModel"] ?? "google/gemini-2.5-flash";

    /// <summary>Model used to parse standalone image uploads (photos, scans).</summary>
    private string GetImageModel()
        => _config["OpenRouter:ImageModel"] ?? "openai/gpt-4o";

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

/// <summary>Questions parsed from a PDF plus which path produced them (for logs/debugging).</summary>
public record PdfParseResult(List<ParsedQuestion> Questions, string PathUsed, int ExtractedChars);

/// <summary>Parsed question returned by GPT.</summary>
public class ParsedQuestion
{
    // Lenient converters: the model's JSON is loosely typed (see LenientJsonConverters).
    [JsonConverter(typeof(LenientIntConverter))]
    public int           Number     { get; set; }
    [JsonConverter(typeof(LenientStringConverter))]
    public string        Title      { get; set; } = string.Empty;
    [JsonConverter(typeof(LenientStringConverter))]
    public string        Type       { get; set; } = "ShortAnswer";
    [JsonConverter(typeof(LenientStringListConverter))]
    public List<string>  Options    { get; set; } = new();
    [JsonConverter(typeof(LenientStringConverter))]
    public string        AnswerKey  { get; set; } = string.Empty;
    [JsonConverter(typeof(LenientStringConverter))]
    public string        Difficulty { get; set; } = "Medium";

    /// <summary>True when the AI believes the source material referenced a
    /// diagram/figure that plain text extraction couldn't carry over.</summary>
    [JsonConverter(typeof(LenientBoolConverter))]
    public bool    HasDiagram        { get; set; }

    /// <summary>One-sentence guess at what a missing diagram shows, so the
    /// teacher knows what to attach via the existing image-upload button.</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string? DiagramHint       { get; set; }

    /// <summary>Set by the AI when a question looks incomplete/ambiguous as
    /// extracted or generated — shown as a warning until the teacher dismisses it.</summary>
    [JsonConverter(typeof(LenientStringConverter))]
    public string? ValidationWarning { get; set; }
}
