using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// Translates question text and feedback for students whose class has
/// translation assistance enabled. Uses the same OpenRouter model as grading —
/// one AI integration, kept consistent across the app.
/// </summary>
public class TranslationService
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TranslationService> _logger;

    public TranslationService(
        IConfiguration config, IHttpClientFactory httpFactory, ILogger<TranslationService> logger)
    {
        _config      = config;
        _httpFactory = httpFactory;
        _logger      = logger;
    }

    private const string SystemPrompt = """
        You translate school assignment text for students. You will be given a target
        language and a piece of text (a question, an explanation, or feedback on an
        answer). Translate it faithfully into the target language.
        - Preserve numbers, mathematical notation, LaTeX (anything between $ signs),
          and formatting exactly as given — translate only the surrounding prose.
        - Keep the same tone and level of formality as the source.
        - Return ONLY the translated text. No preamble, no quotes, no explanation.
        """;

    public async Task<string> TranslateAsync(string text, string targetLanguageName)
    {
        var apiKey  = _config["OpenRouter:ApiKey"]  ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        var model   = _config["OpenRouter:Model"]   ?? "openai/gpt-4o";

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "OpenRouter API key is not configured. Add 'OpenRouter:ApiKey' to appsettings.");

        var payload = new
        {
            model,
            temperature = 0.2,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user",   content = $"Target language: {targetLanguageName}\n\nText:\n{text}" }
            }
        };

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        _logger.LogInformation("Translating text to {Language} via OpenRouter …", targetLanguageName);

        var response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions", content);
        var body     = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OpenRouter returned {Status}: {Body}", response.StatusCode, body);
            throw new Exception($"Translation returned {(int)response.StatusCode}.");
        }

        using var doc = JsonDocument.Parse(body);
        var translated = doc.RootElement
                             .GetProperty("choices")[0]
                             .GetProperty("message")
                             .GetProperty("content")
                             .GetString() ?? "";

        return translated.Trim();
    }
}
