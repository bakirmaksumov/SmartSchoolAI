using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.Services;

/// <summary>
/// Reads photos of handwritten work and marks answers, routing each stage to its model:
///   photo → <see cref="GradingModels.Recognition"/> (Gemini) transcribes the work line by line, exactly as written;
///   then  → <see cref="GradingModels.Grading"/> (GPT-5.5) marks it against the question and the teacher's scheme,
///           seeing the original photo next to the transcription and the steps the student confirmed.
/// Typed answers go straight to the grading model (see <see cref="AnswerGradingService"/>); choice answers with a
/// key never reach the AI.
///
/// Both stages are stored once they succeed (<see cref="AiStageResult"/>), keyed by a hash of everything they depend
/// on, so the same photo is read once, a retry after a failed grading only repeats the grading, and a changed mark
/// scheme re-marks without reading the photo again. Two identical requests at the same time (a double click, two
/// tabs) wait for one another and share one call.
/// </summary>
public class AiMarkingService
{
    /// <summary>Change when the reading instructions change: earlier readings then no longer match.</summary>
    public const string RecognitionPromptVersion = "read-v2";
    /// <summary>Change when the marking instructions change.</summary>
    public const string GradingPromptVersion = "mark-v2";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly AiUsageRecorder _store;
    private readonly OpenRouterClient _ai;
    private readonly GradingModels _models;
    private readonly ILogger<AiMarkingService> _logger;

    // Stored stages are read and written on their own connection, so caching never touches the caller's unsaved work.
    public AiMarkingService(AiUsageRecorder store, OpenRouterClient ai, GradingModels models, ILogger<AiMarkingService> logger)
    {
        _store = store;
        _ai = ai;
        _models = models;
        _logger = logger;
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    private const string RecognitionSystemPrompt = """
        You are an OCR engine for handwritten maths. Transcribe the student's work exactly as written, line by line,
        in the order it appears across all pages. Do NOT judge it, do NOT solve the problem and do NOT fix anything.

        Exactness:
        - Keep every sign, power, fraction, root, bracket, decimal point and unit exactly as the student wrote them.
          A student's mistake stays a mistake: if they wrote "2 + 2 = 5", transcribe "2 + 2 = 5".
        - A minus sign matters: never drop or add one. "−3" and "3" are different.
        - A handwritten "?" is a question mark, never a digit.
        - Corrections: when the student crossed something out and wrote a replacement, transcribe the line as it
          finally stands and put the crossed-out part in "crossedOut". Leave out lines that are entirely crossed out.
        - Illegible or genuinely ambiguous symbols: write your best reading in the line, mark the place with [?] right
          after it, and list it in "unclear" with the possible readings. Never resolve an ambiguity silently.

        Formatting: wrap mathematics in $...$ (use $$...$$ for multi-line constructs); plain words stay outside.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "detectedProblem": "<the problem if visible, else empty>",
          "steps": [ { "number": 1, "text": "<the line as written>", "crossedOut": "", "unclear": [ { "fragment": "<what is unclear>", "readings": ["-3", "3"], "note": "<why>" } ] } ]
        }
        """;

    private const string GradingSystemPrompt = """
        You are an expert math teacher marking one student answer the way an exam mark scheme does.

        You receive the question, the marks available, the expected answer and the teacher's mark scheme when there is
        one. For handwritten work you also receive the ORIGINAL PHOTO(S) of the student's work, the lines as a
        text-recognition step read them, and the student's steps (which the student may have edited).

        The photo is the authority. Compare the transcription and the student's steps with the photo line by line.
        Mark what the student actually wrote on the photo. Where the transcription or the edited steps differ from
        the photo (for example the photo shows "−3" but the text says "3"), mark the photo and report the difference
        in "transcriptionIssues". If a symbol cannot be read with confidence AND the marks depend on it, set
        "needsTeacher" to true and write "teacherNote" naming the line, the symbol and the marks at stake, e.g.
        "Line 3: the sign before 4 is unclear. 1 mark depends on it."

        Marking:
        - Use the teacher's scheme when given: one criterion per mark point (e.g. M1 method, A1 accuracy), with its
          marks. Without a scheme, use a standard one: method marks for a correct method, accuracy marks for correct
          results, so a correct method with an arithmetic slip at the end keeps its method marks.
        - Accept equivalent forms and equivalent wording ("x = 2" and "2", "0.5" and "1/2").
        - Judge mathematics, not handwriting or neatness. Never invent an error.
        - First solve the task yourself so you know the correct final answer and, for domain/inequality problems,
          the correct boundary conditions. Domain rules: an even root's radicand must be ≥ 0 (it MAY equal zero); a
          denominator must be ≠ 0, so a radicand under a root in the denominator must be > 0 (strict). Include the
          boundary (≥) unless it makes a denominator zero.
        - For every criterion give the marks awarded, a short reason, and the piece of work it rests on (quote the
          line, with its number).

        Checking the steps one by one:
        - Check EVERY step on its own, in order. For an algebraic step, expand or simplify it yourself and compare it
          with what the task or the previous step implies. Never accept a step because it looks like the expected
          answer.
        - A step that is itself an error is "incorrect", and its explanation shows the check in one short sentence,
          for example "Expanding gives 2x² − 16x + 14, not 2x² − 8x + 14." Do not mark later steps wrong only because
          an earlier step was; mark them by their own merit (follow-through).
        - "overall" is about the final result. A wrong step does not by itself make it "incorrect", and a correct
          final line does not make a wrong step correct.

        Transformations of graphs:
        - Apply the student's operations to the function IN THE ORDER GIVEN, then expand the result and compare it
          with the target function. Translation by (a, b): y = f(x − a) + b. Stretch parallel to the y-axis by factor
          k: y = k·f(x). Stretch parallel to the x-axis by factor k: y = f(x/k). Reflection in the x-axis: y = −f(x);
          in the y-axis: y = f(−x).
        - The order changes the numbers: for example "translate by (2, 3), then stretch by factor 2 in the
          y-direction" and "stretch by factor 2 in the y-direction, then translate by (2, 6)" give the same function.
          Accept every sequence whose result equals the target; the expected answer shows only one of them.
        - "awardedMarks" is the total, never more than the marks available.
        - Write reasons and the summary in simple, clear English for a student still learning English.
        - Keep $...$ around any maths you write.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "criteria": [ { "criterion": "M1 correct method", "maxMarks": 1, "awarded": 1, "explanation": "<why>", "evidence": "<line N: quoted work>", "step": 2 } ],
          "awardedMarks": 0,
          "maxMarks": 0,
          "steps": [ { "number": 1, "text": "<step>", "status": "correct|incorrect", "explanation": "<why, only if incorrect>" } ],
          "overall": "correct|partial|incorrect",
          "expectedAnswer": "<the correct final answer>",
          "summary": "<one sentence for the student>",
          "transcriptionIssues": [ { "step": 3, "transcribed": "3", "onPhoto": "-3", "certain": true, "marksAffected": 1, "note": "<short>" } ],
          "needsTeacher": false,
          "teacherNote": ""
        }
        """;

    // ── Results ──────────────────────────────────────────────────────────────

    public sealed record Page(byte[] Bytes, string Mime);

    public sealed class Recognition
    {
        public string Key { get; set; } = "";
        public string Model { get; set; } = "";
        public string DetectedProblem { get; set; } = "";
        public List<RecognizedStep> Steps { get; set; } = new();
        [JsonIgnore] public bool FromCache { get; set; }
    }

    public sealed class RecognizedStep
    {
        public int Number { get; set; }
        public string Text { get; set; } = "";
        public string? CrossedOut { get; set; }
        public List<UnclearFragment> Unclear { get; set; } = new();
    }

    public sealed class UnclearFragment
    {
        public string Fragment { get; set; } = "";
        public List<string> Readings { get; set; } = new();
        public string? Note { get; set; }
    }

    public sealed class Criterion
    {
        [JsonPropertyName("criterion")] public string Name { get; set; } = "";
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal MaxMarks { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal Awarded { get; set; }
        public string Explanation { get; set; } = "";
        public string? Evidence { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int? Step { get; set; }
    }

    public sealed class TranscriptionIssue
    {
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public int? Step { get; set; }
        public string? Transcribed { get; set; }
        public string? OnPhoto { get; set; }
        [JsonConverter(typeof(LenientBoolConverter))] public bool Certain { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? MarksAffected { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>What the grading model decided, as stored for the teacher (<see cref="StudentAnswer.GradingJson"/>).</summary>
    public sealed class Verdict
    {
        public string Key { get; set; } = "";
        public string Model { get; set; } = "";
        public List<Criterion> Criteria { get; set; } = new();
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? AwardedMarks { get; set; }
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public decimal? MaxMarks { get; set; }
        public List<HandwritingStepDto> Steps { get; set; } = new();
        public string Overall { get; set; } = "";
        public string ExpectedAnswer { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<TranscriptionIssue> TranscriptionIssues { get; set; } = new();
        [JsonConverter(typeof(LenientBoolConverter))] public bool NeedsTeacher { get; set; }
        public string? TeacherNote { get; set; }
        /// <summary>The steps as first read from the photo (for the teacher's side-by-side view).</summary>
        public List<RecognizedStep>? RecognizedSteps { get; set; }
        public string? RecognitionModel { get; set; }
        /// <summary>The steps as the student confirmed or typed them (what was marked, and what a retry marks again).</summary>
        public List<RecognizedStep>? StudentSteps { get; set; }
        /// <summary>The independent second check, when it ran (part of the same stored grading).</summary>
        public GradingVerificationDto? Verification { get; set; }
        [JsonIgnore] public bool FromCache { get; set; }

        /// <summary>
        /// The marks this verdict gives out of <paramref name="max"/>: the sum of the criteria when there are any,
        /// otherwise the stated total, otherwise all or nothing on the overall verdict. Never outside 0..max, in
        /// half marks.
        /// </summary>
        public decimal Points(decimal max)
        {
            decimal raw = Criteria.Count > 0
                ? Criteria.Sum(c => Math.Clamp(c.Awarded, 0m, c.MaxMarks > 0 ? c.MaxMarks : max))
                : AwardedMarks ?? (string.Equals(Overall, "correct", StringComparison.OrdinalIgnoreCase) ? max : 0m);
            return Math.Clamp(Math.Round(raw * 2, MidpointRounding.AwayFromZero) / 2, 0m, max);
        }
    }

    /// <summary>Everything a grading depends on. Its hash is the grading's cache key.</summary>
    public sealed record GradeRequest(
        string QuestionText, decimal MaxMarks, string? AnswerKey, string? MarkScheme,
        IReadOnlyList<HandwritingStepDto> StudentSteps,
        IReadOnlyList<Page> Photos, Recognition? Recognized,
        Page? ReferenceImage = null);

    // ── 1. Reading a photo ───────────────────────────────────────────────────

    /// <summary>The steps written on these page(s), read once per photo, model and instruction version.</summary>
    public async Task<Recognition> RecognizeAsync(IReadOnlyList<Page> pages, string questionText, AiCallContext context)
    {
        var model = _models.Recognition;
        var key = "rec:" + Hash(model, RecognitionPromptVersion, questionText, string.Join(",", pages.Select(p => Hash(p.Bytes))));

        return await OnceAsync(key, async () =>
        {
            var task = string.IsNullOrWhiteSpace(questionText) ? "" : $"The assigned task is:\n'{questionText}'\n\n";
            var content = new List<object>();
            foreach (var p in pages)
                content.Add(new { type = "image_url", image_url = new { url = DataUri(p), detail = "high" } });
            content.Add(new { type = "text", text = task + (pages.Count > 1 ? $"These {pages.Count} images are the pages of one handwritten solution, in order. " : "The image is a handwritten solution. ")
                                                       + "Transcribe every written line, in order." });

            var reply = await _ai.CompleteAsync(model, new object[]
            {
                new { role = "system", content = RecognitionSystemPrompt },
                new { role = "user", content = content.ToArray() }
            }, context with { Stage = "recognition", CacheKey = key }, temperature: 0.1, timeout: _models.Timeout);

            var parsed = Parse<Recognition>(reply.Content, "recognition");
            parsed.Steps.RemoveAll(s => string.IsNullOrWhiteSpace(s.Text));
            for (var i = 0; i < parsed.Steps.Count; i++) if (parsed.Steps[i].Number <= 0) parsed.Steps[i].Number = i + 1;
            parsed.Key = key;
            parsed.Model = model;
            return parsed;
        }, "recognition", model, RecognitionPromptVersion);
    }

    /// <summary>A reading already stored for these pages, without calling anyone; null when there is none.</summary>
    public async Task<Recognition?> FindRecognitionAsync(IReadOnlyList<Page> pages, string questionText)
    {
        var key = "rec:" + Hash(_models.Recognition, RecognitionPromptVersion, questionText, string.Join(",", pages.Select(p => Hash(p.Bytes))));
        await using var db = _store.NewContext();
        var row = await db.AiStageResults.AsNoTracking().FirstOrDefaultAsync(x => x.CacheKey == key);
        if (row is null) return null;
        var r = JsonSerializer.Deserialize<Recognition>(row.ResultJson, Json)!;
        r.FromCache = true;
        return r;
    }

    // ── 2. Marking ───────────────────────────────────────────────────────────

    /// <summary>Marks one answer (steps, with the photo when there is one), once per set of inputs.</summary>
    public async Task<Verdict> GradeAsync(GradeRequest request, AiCallContext context,
                                          Func<Verdict, string, Task<GradingVerificationDto?>>? verify = null)
    {
        var model = _models.Grading;
        var key = GradingKey(request);

        return await OnceAsync(key, async () =>
        {
            var text = new StringBuilder();
            text.Append($"Task:\n{request.QuestionText}\n").Append('\n');
            text.Append($"Marks available: {request.MaxMarks:0.##}").Append('\n');
            text.Append($"Expected answer: {(string.IsNullOrWhiteSpace(request.AnswerKey) ? "(not provided)" : request.AnswerKey)}").Append('\n');
            text.Append($"Teacher's mark scheme: {(string.IsNullOrWhiteSpace(request.MarkScheme) ? "(none — use a standard scheme)" : request.MarkScheme)}\n").Append('\n');

            var content = new List<object>();
            if (request.Photos.Count > 0)
            {
                content.Add(new { type = "text", text = $"ORIGINAL PHOTO(S) OF THE STUDENT'S WORK ({request.Photos.Count} page(s)) — the authority:" });
                foreach (var p in request.Photos)
                    content.Add(new { type = "image_url", image_url = new { url = DataUri(p), detail = "high" } });
            }
            if (request.ReferenceImage is { } refImg)
            {
                content.Add(new { type = "text", text = "TEACHER'S REFERENCE SOLUTION (not the student's work):" });
                content.Add(new { type = "image_url", image_url = new { url = DataUri(refImg), detail = "high" } });
            }

            if (request.Recognized is { } rec)
            {
                text.Append("Lines as read from the photo by OCR:").Append('\n');
                foreach (var s in rec.Steps)
                {
                    text.Append($"{s.Number}. {s.Text}");
                    if (!string.IsNullOrWhiteSpace(s.CrossedOut)) text.Append($"   (crossed out: {s.CrossedOut})");
                    foreach (var u in s.Unclear) text.Append($"   [unclear: {u.Fragment} — could be {string.Join(" or ", u.Readings)}]");
                    text.Append('\n');
                }
                text.Append('\n');
            }
            text.Append("Student's steps:").Append('\n');
            foreach (var s in request.StudentSteps) text.Append($"{s.Number}. {s.Text}").Append('\n');
            content.Add(new { type = "text", text = text.ToString() });

            var reply = await _ai.CompleteAsync(model, new object[]
            {
                new { role = "system", content = GradingSystemPrompt },
                new { role = "user", content = content.ToArray() }
            }, context with { Stage = "grading", CacheKey = key }, temperature: _models.GradingTemperature, timeout: _models.Timeout);

            var verdict = Parse<Verdict>(reply.Content, "grading");
            verdict.Key = key;
            verdict.Model = model;
            verdict.RecognizedSteps = request.Recognized?.Steps;
            verdict.RecognitionModel = request.Recognized?.Model;
            if (verify is not null) verdict.Verification = await verify(verdict, key);
            return verdict;
        }, "grading", model, GradingPromptVersion);
    }

    public string GradingKey(GradeRequest r) => "grade:" + Hash(
        _models.Grading, GradingPromptVersion, r.QuestionText, r.MaxMarks.ToString(System.Globalization.CultureInfo.InvariantCulture),
        r.AnswerKey ?? "", r.MarkScheme ?? "",
        string.Join("\n", r.StudentSteps.Select(s => $"{s.Number}:{s.Text}")),
        r.Recognized?.Key ?? "", string.Join(",", r.Photos.Select(p => Hash(p.Bytes))),
        r.ReferenceImage is { } img ? Hash(img.Bytes) : "");

    // ── Shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs <paramref name="work"/> at most once per key: a stored result is returned without a call; two callers
    /// with the same key at the same time share one call. Only a success is stored, so a failure can be retried.
    /// </summary>
    private async Task<T> OnceAsync<T>(string key, Func<Task<T>> work, string stage, string model, string promptVersion) where T : class
    {
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            AiStageResult? stored;
            await using (var db = _store.NewContext())
                stored = await db.AiStageResults.AsNoTracking().FirstOrDefaultAsync(x => x.CacheKey == key);
            if (stored is not null)
            {
                var cached = JsonSerializer.Deserialize<T>(stored.ResultJson, Json)!;
                if (cached is Recognition r) r.FromCache = true;
                if (cached is Verdict v) v.FromCache = true;
                _logger.LogInformation("AI stage {Stage} reused key={Key}", stage, key);
                return cached;
            }

            var fresh = await work();
            try
            {
                await using var db = _store.NewContext();
                db.AiStageResults.Add(new AiStageResult
                {
                    CacheKey = key, Stage = stage, Model = model, PromptVersion = promptVersion,
                    ResultJson = JsonSerializer.Serialize(fresh)
                });
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another server process stored the same key first: theirs and ours are equivalent.
            }
            return fresh;
        }
        finally
        {
            gate.Release();
        }
    }

    private T Parse<T>(string raw, string stage) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(OpenRouterClient.CleanJson(raw), Json)
                   ?? throw new AiCallException("bad-response", $"The AI's {stage} reply was empty.");
        }
        catch (JsonException ex)
        {
            throw new AiCallException("bad-response", $"The AI's {stage} reply could not be read.", ex);
        }
    }

    public string GradingModel => _models.Grading;
    public string RecognitionModel => _models.Recognition;

    public static string Marks(decimal v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string DataUri(Page p) => $"data:{p.Mime};base64,{Convert.ToBase64String(p.Bytes)}";

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..32];

    public static string Hash(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", parts))))[..40];
}
