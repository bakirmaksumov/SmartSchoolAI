namespace SmartSchoolAI.Models;

/// <summary>
/// Stores an AI tutor chat session tied to a student's submission.
/// Messages are stored as a JSON array: [{role, content, timestamp}].
/// Mode controls the AI behaviour: hint | explain | reattempt.
/// </summary>
public class ChatSession : BaseEntity
{
    public string Mode     { get; set; } = "hint";
    public string Messages { get; set; } = "[]";

    // FK
    public Guid StudentResultId { get; set; }

    // Navigation
    public StudentResult StudentResult { get; set; } = null!;
}
