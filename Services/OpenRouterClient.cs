using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>What a call is for: the stage, the stage result it belongs to and whose work it is (ids only).</summary>
public sealed record AiCallContext(
    string Stage, string? CacheKey = null, Guid? StudentId = null, Guid? AssignmentId = null,
    Guid? ResultId = null, string? Questions = null);

/// <summary>The model's reply and what OpenRouter said it cost.</summary>
public sealed record AiReply(string Content, string Model, int? InputTokens, int? OutputTokens, decimal? Cost, string? GenerationId);

/// <summary>An AI call that did not give a usable answer. The work it was for is kept; the stage can be retried.</summary>
public sealed class AiCallException : Exception
{
    /// <summary>timeout | unavailable | bad-response | not-configured</summary>
    public string Kind { get; }
    public AiCallException(string kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
}

/// <summary>Which models check student work. Kept apart from <c>OpenRouter:Model</c>, which the tutor's hints,
/// translation and question import also use.</summary>
public sealed class GradingModels
{
    /// <summary>Reads photos of handwritten work (OpenRouter:Grading:RecognitionModel).</summary>
    public string Recognition { get; }
    /// <summary>Marks answers against the question and the teacher's scheme (OpenRouter:Grading:GradingModel).</summary>
    public string Grading { get; }
    /// <summary>Temperature for grading; null = the model's default (GPT-5 models accept only their default).</summary>
    public double? GradingTemperature { get; }
    public TimeSpan Timeout { get; }

    public GradingModels(IConfiguration config)
    {
        Recognition = Non(config["OpenRouter:Grading:RecognitionModel"]) ?? "google/gemini-3.1-pro-preview";
        Grading     = Non(config["OpenRouter:Grading:GradingModel"]) ?? "openai/gpt-5.5";
        GradingTemperature = double.TryParse(config["OpenRouter:Grading:Temperature"], System.Globalization.NumberStyles.Float,
                                             System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;
        Timeout = TimeSpan.FromSeconds(Math.Clamp(config.GetValue("OpenRouter:Grading:TimeoutSeconds", 180), 10, 600));
    }

    private static string? Non(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// The one way grading talks to OpenRouter. Every request asks OpenRouter to report usage, and every call — successful
/// or not — is written to <see cref="AiCallLog"/> with its stage, model, tokens and cost. Nothing of the student's
/// work goes into the log or the application log, only ids.
/// </summary>
public class OpenRouterClient
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _http;
    private readonly AiUsageRecorder _usage;
    private readonly ILogger<OpenRouterClient> _logger;

    public OpenRouterClient(IConfiguration config, IHttpClientFactory http, AiUsageRecorder usage, ILogger<OpenRouterClient> logger)
    {
        _config = config;
        _http   = http;
        _usage  = usage;
        _logger = logger;
    }

    public async Task<AiReply> CompleteAsync(
        string model, object[] messages, AiCallContext context, double? temperature = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var apiKey  = _config["OpenRouter:ApiKey"] ?? "";
        var baseUrl = _config["OpenRouter:BaseUrl"] ?? "https://openrouter.ai/api/v1";
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiCallException("not-configured", "OpenRouter API key is not configured. Add 'OpenRouter:ApiKey' to appsettings.");

        var payload = new Dictionary<string, object>
        {
            ["model"]    = model,
            ["messages"] = messages,
            ["usage"]    = new { include = true }      // OpenRouter then reports tokens and cost in the reply
        };
        if (temperature is { } t) payload["temperature"] = t;

        var log = new AiCallLog
        {
            CacheKey = context.CacheKey, Stage = context.Stage, Model = model, StudentId = context.StudentId,
            AssignmentId = context.AssignmentId, ResultId = context.ResultId, Questions = context.Questions
        };
        var watch = Stopwatch.StartNew();
        try
        {
            using var http = _http.CreateClient("openrouter");
            http.Timeout = timeout ?? TimeSpan.FromMinutes(3);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            HttpResponseMessage response;
            try
            {
                response = await http.PostAsync($"{baseUrl.TrimEnd('/')}/chat/completions",
                    new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiCallException("timeout", "The AI did not answer in time.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new AiCallException("unavailable", "The AI service could not be reached.", ex);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new AiCallException("unavailable", $"The AI service returned {(int)response.StatusCode}.");

            string content;
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                log.GenerationId = root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    log.InputTokens  = Int(usage, "prompt_tokens");
                    log.OutputTokens = Int(usage, "completion_tokens");
                    log.Cost         = usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
            {
                throw new AiCallException("bad-response", "The AI service sent a reply that could not be read.", ex);
            }

            log.Succeeded = true;
            return new AiReply(content, model, log.InputTokens, log.OutputTokens, log.Cost, log.GenerationId);
        }
        catch (AiCallException ex)
        {
            log.Failure = ex.Kind;
            throw;
        }
        finally
        {
            log.DurationMs = (int)watch.ElapsedMilliseconds;
            _logger.LogInformation(
                "AI call stage={Stage} model={Model} ok={Ok} failure={Failure} in={In} out={Out} cost={Cost} gen={Gen} key={Key} result={Result} q={Questions} ms={Ms}",
                log.Stage, log.Model, log.Succeeded, log.Failure, log.InputTokens, log.OutputTokens, log.Cost, log.GenerationId,
                log.CacheKey, log.ResultId, log.Questions, log.DurationMs);
            await _usage.RecordAsync(log);
        }
    }

    private static int? Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    /// <summary>The JSON in a model reply: without markdown fences, with LaTeX backslashes made valid.</summary>
    public static string CleanJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```")) raw = raw[(raw.IndexOf('\n') + 1)..];
        if (raw.EndsWith("```"))   raw = raw[..raw.LastIndexOf("```")];
        return JsonSanitizer.FixInvalidBackslashes(raw.Trim());
    }
}

/// <summary>Writes <see cref="AiCallLog"/> rows on their own connection, so a call is recorded even when the work
/// it was for fails or is rolled back.</summary>
public class AiUsageRecorder
{
    private readonly Func<AppDbContext> _newContext;
    private readonly ILogger<AiUsageRecorder>? _logger;

    public AiUsageRecorder(Func<AppDbContext> newContext, ILogger<AiUsageRecorder>? logger = null)
    {
        _newContext = newContext;
        _logger = logger;
    }

    /// <summary>A context on its own connection (used for the stored AI stages too).</summary>
    public AppDbContext NewContext() => _newContext();

    public async Task RecordAsync(AiCallLog log)
    {
        try
        {
            await using var db = _newContext();
            db.AiCallLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Cost tracking must never break grading.
            _logger?.LogWarning(ex, "Could not record AI call {Stage} {Model}", log.Stage, log.Model);
        }
    }
}
