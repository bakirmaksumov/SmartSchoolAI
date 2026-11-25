using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>
/// Stands in for OpenRouter. Records every request — the model, the system instruction, the text and the images
/// actually sent — and answers from a script, with token counts and a cost like OpenRouter reports them.
/// </summary>
public sealed class FakeOpenRouter : IHttpClientFactory
{
    public sealed record Request(string Model, string System, string Text, List<string> Images, string RawJson)
    {
        public string Kind => System.Contains("OCR engine") ? "recognition"
                            : System.Contains("exam moderator") ? "verification"
                            : System.Contains("grading students' typed answers") ? "typed"
                            : System.Contains("expert math teacher") ? "grading"
                            : "other";
    }

    public ConcurrentQueue<Request> Requests { get; } = new();
    public List<Request> All => Requests.ToList();

    /// <summary>The model's reply (the JSON content) for a request; set per test.</summary>
    public Func<Request, string> Reply { get; set; } = _ => "{}";
    /// <summary>When set and returns a status, the call fails with it (e.g. 500, 503).</summary>
    public Func<Request, HttpStatusCode?> Fail { get; set; } = _ => null;
    /// <summary>Simulated latency, for the "two clicks at once" test.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;
    /// <summary>Cost reported per call; null = OpenRouter reports none.</summary>
    public decimal? CostPerCall { get; set; } = 0.0125m;

    public HttpClient CreateClient(string name) => new(new Handler(this));

    private sealed class Handler : HttpMessageHandler
    {
        private readonly FakeOpenRouter _owner;
        public Handler(FakeOpenRouter owner) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken ct)
        {
            var raw = await message.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var system = new StringBuilder();
            var text = new StringBuilder();
            var images = new List<string>();
            foreach (var m in root.GetProperty("messages").EnumerateArray())
            {
                var role = m.GetProperty("role").GetString();
                var content = m.GetProperty("content");
                if (content.ValueKind == JsonValueKind.String)
                    (role == "system" ? system : text).AppendLine(content.GetString());
                else
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.GetProperty("type").GetString() == "image_url")
                            images.Add(part.GetProperty("image_url").GetProperty("url").GetString()!);
                        else
                            text.AppendLine(part.GetProperty("text").GetString());
                    }
            }
            var request = new Request(root.GetProperty("model").GetString()!, system.ToString(), text.ToString(), images, raw);
            _owner.Requests.Enqueue(request);

            if (_owner.Delay > TimeSpan.Zero) await Task.Delay(_owner.Delay, ct);
            if (_owner.Fail(request) is { } status)
                return new HttpResponseMessage(status) { Content = new StringContent("{\"error\":{\"message\":\"mock failure\"}}") };

            var usage = new Dictionary<string, object> { ["prompt_tokens"] = 1200, ["completion_tokens"] = 300 };
            if (_owner.CostPerCall is { } cost) usage["cost"] = cost;
            var body = JsonSerializer.Serialize(new
            {
                id = "gen-" + Guid.NewGuid().ToString("N")[..12],
                choices = new[] { new { message = new { role = "assistant", content = _owner.Reply(request) } } },
                usage
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
