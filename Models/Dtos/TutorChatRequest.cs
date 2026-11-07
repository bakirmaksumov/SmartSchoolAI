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

    /// <summary>Prior conversation history.</summary>
    public List<ChatMessageDto> History { get; set; } = new();
}
