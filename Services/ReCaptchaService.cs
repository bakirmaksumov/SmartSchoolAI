using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// Verifies Google reCAPTCHA v2 ("I'm not a robot") tokens submitted from the
/// login form against Google's siteverify endpoint.
/// </summary>
public class ReCaptchaService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ReCaptchaService> _logger;

    public ReCaptchaService(
        IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ReCaptchaService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    /// <summary>Site key used by the view to render the widget. Empty until configured.</summary>
    public string SiteKey => _config["ReCaptcha:SiteKey"] ?? "";

    /// <summary>
    /// When no secret key is configured, verification is skipped (returns true) so
    /// local/dev environments keep working before the keys are registered with Google.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["ReCaptcha:SecretKey"]);

    public async Task<bool> VerifyAsync(string? token, string? remoteIp)
    {
        if (!IsConfigured)
            return true;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        var secretKey = _config["ReCaptcha:SecretKey"];

        var http = _httpClientFactory.CreateClient();
        var form = new Dictionary<string, string>
        {
            ["secret"]   = secretKey!,
            ["response"] = token
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
            form["remoteip"] = remoteIp;

        try
        {
            var response = await http.PostAsync(
                "https://www.google.com/recaptcha/api/siteverify",
                new FormUrlEncodedContent(form));

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);

            return doc.RootElement.TryGetProperty("success", out var success)
                   && success.GetBoolean();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reCAPTCHA verification request failed.");
            return false;
        }
    }
}
