namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// Request body for the /Student/ChatStream SSE endpoint.
/// </summary>
public sealed class TutorChatRequest
{
    /// <summary>The user's message text.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Chat mode: "hint", "concept", or "solution".</summary>
    public string Mode { get; set; } = "hint";

    /// <summary>Optional assignment ID for question context.</summary>
    public string? AssignmentId { get; set; }

    /// <summary>Index of the question the student is working on, so help is about that question.</summary>
    public int? QuestionIndex { get; set; }

    /// <summary>The student's latest answer to the active question (as currently in the answer box).</summary>
    public string? Answer { get; set; }

    /// <summary>The student's latest working for the active question, one entry per step.</summary>
    public List<string>? Work { get; set; }

    /// <summary>Set when help was opened from one step: its number, text and the checker's feedback.</summary>
    public int?    StepNumber   { get; set; }
    public string? StepText     { get; set; }
    public string? StepFeedback { get; set; }

    /// <summary>Prior conversation history.</summary>
    public List<ChatMessageDto> History { get; set; } = new();
}
