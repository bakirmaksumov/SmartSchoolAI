namespace SmartSchoolAI.Models;

/// <summary>
/// A finished, successful AI stage (reading a photo, or grading an answer), kept so the same request is never paid
/// for twice. <see cref="CacheKey"/> is a hash of everything the result depends on — for a reading: the photo bytes,
/// the model and the version of the instructions; for a grading: the question, the key and mark scheme, the answer
/// or its steps, the photo, the model and the instructions. A new photo or a changed mark scheme gives a new key.
/// Only successful results are stored, so a failed stage is simply tried again.
/// </summary>
public class AiStageResult
{
    public Guid     Id            { get; set; } = Guid.NewGuid();
    public string   CacheKey      { get; set; } = string.Empty;
    /// <summary>recognition | grading</summary>
    public string   Stage         { get; set; } = string.Empty;
    public string   Model         { get; set; } = string.Empty;
    public string   PromptVersion { get; set; } = string.Empty;
    public string   ResultJson    { get; set; } = "{}";
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// One request actually sent to the AI provider: which stage, which model, the tokens and the cost OpenRouter
/// reported (null when it did not). Holds technical ids only — never the photo or the text of an answer.
/// </summary>
public class AiCallLog
{
    public Guid     Id            { get; set; } = Guid.NewGuid();
    /// <summary>The stage result this call produced or belonged to (recognition, grading and its verification).</summary>
    public string?  CacheKey      { get; set; }
    /// <summary>recognition | grading | verification | typed-grading | answer-check | sketch</summary>
    public string   Stage         { get; set; } = string.Empty;
    public string   Model         { get; set; } = string.Empty;
    public int?     InputTokens   { get; set; }
    public int?     OutputTokens  { get; set; }
    public decimal? Cost          { get; set; }
    /// <summary>OpenRouter's generation id, to look the call up in its dashboard.</summary>
    public string?  GenerationId  { get; set; }
    public bool     Succeeded     { get; set; }
    /// <summary>timeout | unavailable | bad-response — no message text from the provider.</summary>
    public string?  Failure       { get; set; }
    public int      DurationMs    { get; set; }
    public Guid?    StudentId     { get; set; }
    public Guid?    AssignmentId  { get; set; }
    public Guid?    ResultId      { get; set; }
    /// <summary>The question(s) the call was about: "3", or "1,4" for a batch of typed answers.</summary>
    public string?  Questions     { get; set; }
    public DateTime CreatedAt     { get; set; } = DateTime.UtcNow;
}
