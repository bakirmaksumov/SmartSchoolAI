using System.Text;
using System.Text.Json;
using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.Services;

/// <summary>
/// The extra AI checks around handwritten and drawn work:
///   * <see cref="VerifyEvaluationAsync"/> — an independent second check of a grade (OpenRouter:EnableGradingVerification,
///     on by default), sent to the grading model; its cost is recorded with the grading it checks;
///   * <see cref="GradeSketchAsync"/> — marks a drawing on a diagram with the vision model (OpenRouter:VisionModel).
/// Reading photos and the main marking live in <see cref="AiMarkingService"/>.
/// </summary>
public class HandwritingAnalysisService
{
    private readonly OpenRouterClient _ai;
    private readonly GradingModels _models;
    private readonly IConfiguration _config;
    private readonly ILogger<HandwritingAnalysisService> _logger;

    public HandwritingAnalysisService(OpenRouterClient ai, GradingModels models, IConfiguration config, ILogger<HandwritingAnalysisService> logger)
    {
        _ai     = ai;
        _models = models;
        _config = config;
        _logger = logger;
    }

    /// <summary>True when the second check runs (one more call to the grading model per photo or typed-steps answer).</summary>
    public bool VerificationEnabled => _config.GetValue("OpenRouter:EnableGradingVerification", true);

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

        Also check each of the student's steps yourself, independently of the marker, and give
        your own mark for EVERY step even when you agree with the overall verdict. Expand or
        simplify algebraic steps before judging them. A step the marker passed can still be wrong:
        mark it "incorrect" with a one-sentence reason that shows the check. For transformations,
        apply the operations in the order given and compare the resulting function with the target;
        different valid orders (with correspondingly different numbers) are all correct.

        Report your own verdict, whether you agree with the marker, and how confident
        you are. Use a LOW confidence when the task is ambiguous, the steps are unclear
        or you cannot verify the result. Write notes in simple, clear English.

        Return ONLY valid JSON — no markdown fences, no explanation:
        {
          "overall": "correct|incorrect",
          "agrees": true|false,
          "confidence": 0.0,
          "notes": "<one or two short sentences for the teacher>",
          "steps": [ { "number": 1, "status": "correct|incorrect", "explanation": "<why, only if incorrect>" } ]
        }
        """;

    /// <summary>
    /// Independently re-checks a grade. Returns null when verification is disabled or the call fails — the grade
    /// then stands as it is.
    /// </summary>
    public async Task<GradingVerificationDto?> VerifyEvaluationAsync(
        string questionText, string? answerKey, IEnumerable<HandwritingStepDto> steps, HandwritingAnalysisDto evaluation,
        AiCallContext? context = null)
    {
        if (!VerificationEnabled) return null;

        var stepLines = string.Join("\n", steps.Select(s => $"{s.Number}. {s.Text}"));
        var userText =
            $"Task:\n{questionText}\n\n" +
            $"Expected answer: {(string.IsNullOrWhiteSpace(answerKey) ? "(not provided)" : answerKey)}\n\n" +
            $"Student's steps:\n{stepLines}\n\n" +
            $"The marker's grade was: {evaluation.Overall}\n" +
            $"The marker's reason: {evaluation.Summary}\n" +
            "The marker's mark for each step:\n" +
            string.Join("\n", evaluation.Steps.Select(s =>
                $"{s.Number}. {(string.IsNullOrWhiteSpace(s.Status) ? "unmarked" : s.Status)}" +
                (string.IsNullOrWhiteSpace(s.Explanation) ? "" : $" ({s.Explanation})")));

        try
        {
            var reply = await _ai.CompleteAsync(_models.Grading, new object[]
            {
                new { role = "system", content = VerifySystemPrompt },
                new { role = "user",   content = userText }
            }, (context ?? new AiCallContext("verification")) with { Stage = "verification" },
               temperature: _models.GradingTemperature, timeout: _models.Timeout);

            return JsonSerializer.Deserialize<GradingVerificationDto>(OpenRouterClient.CleanJson(reply.Content),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            // Verification is a safety net — never block grading when it fails.
            _logger.LogWarning("Grading verification failed ({Kind}); keeping the grade.", (ex as AiCallException)?.Kind ?? ex.GetType().Name);
            return null;
        }
    }

    // ── Drawn answers (sketch on a diagram) ──────────────────────────────────

    private const string SketchSystemPrompt = """
        You are an experienced maths examiner marking a student's sketch.
        The image shows the question's diagram (if it had one) with the student's own pen
        strokes drawn on top. Strokes may be black, blue or red; printed parts of the
        diagram were there before the student drew.

        Mark it the way an exam mark scheme marks sketches:
        - Judge the essential features: the right general shape, the right position and
          orientation, symmetry where required (e.g. an inverse function is the reflection
          in y = x), intercepts, turning points and asymptotes the question needs, and the
          right domain (where the curve starts and stops).
        - A hand-drawn sketch is never exact. Do not take marks off for wobbly lines,
          imperfect scale or small inaccuracies that a fair examiner would accept.
        - If the student drew nothing meaningful, or drew something unrelated, give 0.
        - Award whole marks from 0 to the marks available. "correct" is true only when
          the sketch earns full marks.
        - If the drawing is hard to read, or you are not sure, say so with a LOW confidence.
        - Write the feedback in one or two short, simple English sentences for the student.

        Return ONLY valid JSON — no markdown fences, no explanation:
        { "correct": true, "marks": 1, "confidence": 0.0, "feedback": "<short feedback>" }
        """;

    /// <summary>
    /// Marks a drawn answer from the picture (the diagram with the student's strokes).
    /// Throws when the AI call fails; returns the model's verdict otherwise.
    /// </summary>
    public async Task<SketchVerdictDto> GradeSketchAsync(
        string questionText, string? answerKey, decimal maxMarks, byte[] png, string? writtenAnswer, AiCallContext? context = null)
    {
        var userText =
            $"Question:\n{questionText}\n\n" +
            $"Marks available: {maxMarks:0.##}\n" +
            $"Expected answer / mark scheme: {(string.IsNullOrWhiteSpace(answerKey) ? "(not provided — work out what the sketch should show)" : answerKey)}\n" +
            (string.IsNullOrWhiteSpace(writtenAnswer) ? "" : $"The student also wrote: {writtenAnswer}\n") +
            "\nThe image is the student's drawing.";

        var reply = await _ai.CompleteAsync(_config["OpenRouter:VisionModel"] ?? "qwen/qwen2.5-vl-72b-instruct", new object[]
        {
            new { role = "system", content = SketchSystemPrompt },
            new
            {
                role    = "user",
                content = new object[]
                {
                    new { type = "image_url", image_url = new { url = $"data:image/png;base64,{Convert.ToBase64String(png)}", detail = "high" } },
                    new { type = "text", text = userText }
                }
            }
        }, (context ?? new AiCallContext("sketch")) with { Stage = "sketch" }, temperature: 0.1);

        return JsonSerializer.Deserialize<SketchVerdictDto>(OpenRouterClient.CleanJson(reply.Content),
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? throw new AiCallException("bad-response", "The AI reply for the drawing was empty.");
    }
}

/// <summary>The AI's mark for a drawn answer.</summary>
public class SketchVerdictDto
{
    [System.Text.Json.Serialization.JsonConverter(typeof(LenientBoolConverter))]
    public bool     Correct    { get; set; }
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public decimal? Marks      { get; set; }
    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public double   Confidence { get; set; }
    public string   Feedback   { get; set; } = string.Empty;
}
