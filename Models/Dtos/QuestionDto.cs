using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSchoolAI.Models.Dtos;

public class QuestionDto
{
    [JsonPropertyName("id")]
    public string? Id          { get; set; }
    
    [JsonPropertyName("title")]
    public string  Title       { get; set; } = string.Empty;
    
    [JsonPropertyName("type")]
    [JsonConverter(typeof(TypeConverter))]
    public string  Type        { get; set; } = string.Empty;
    
    [JsonPropertyName("answerKey")]
    public string? AnswerKey   { get; set; }
    
    [JsonPropertyName("options")]
    [JsonConverter(typeof(OptionsConverter))]
    public string? Options     { get; set; }  // JSON array for MCQ

    /// <summary>Interactive answer-panel state for visual types (Graphing, NumberLine, …). See <see cref="Question.VisualState"/>.</summary>
    [JsonPropertyName("visualState")]
    public string? VisualState { get; set; }
    
    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
    
    [JsonPropertyName("difficulty")]
    public string? Difficulty  { get; set; }

    [JsonPropertyName("imagePath")]
    public string? ImagePath   { get; set; }

    [JsonPropertyName("answerKeyImagePath")]
    public string? AnswerKeyImagePath { get; set; }

    /// <summary>
    /// Teacher-set per question: when true, the student's "Show your work" panel
    /// is required (checked and locked open) instead of left to the student to
    /// turn on. Distinct from the assignment-wide instant-feedback setting.
    /// </summary>
    [JsonPropertyName("requireWork")]
    public bool RequireWork { get; set; }

    /// <summary>
    /// When set, this question is a subpoint (e.g. "2a") nested under the
    /// question whose Id matches this value — rendered indented under that
    /// parent and numbered relative to it ("2a", "2b", ...), but otherwise a
    /// fully normal, independently answerable/gradeable question in the flat
    /// list. A question referenced by another's GroupId becomes a non-answerable
    /// "group header" purely by virtue of having children — not a stored flag —
    /// so grouping survives reordering without index bookkeeping.
    /// </summary>
    [JsonPropertyName("groupId")]
    public string? GroupId { get; set; }

    /// <summary>
    /// One-sentence guess at a diagram the AI believes this question refers to
    /// but couldn't carry over from a text-only PDF import — prompts the
    /// teacher to attach the real figure via the existing image upload.
    /// </summary>
    [JsonPropertyName("diagramHint")]
    public string? DiagramHint { get; set; }

    /// <summary>
    /// Set by the AI at import/generation time when a question looks
    /// incomplete or ambiguous. Shown as a warning on the editor card until
    /// the teacher dismisses it or edits the question.
    /// </summary>
    [JsonPropertyName("validationWarning")]
    public string? ValidationWarning { get; set; }
}

/// <summary>
/// Converts type field which can be either a number (0, 1, 4) or string ("MultipleChoice", "0", "1")
/// </summary>
internal class TypeConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number => reader.GetInt32().ToString(),
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            _ => string.Empty
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

/// <summary>
/// Converts options field which can be either an array or a JSON string
/// </summary>
internal class OptionsConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.StartArray => System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonSerializer.Deserialize<List<string>>(ref reader, options)),
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else
            writer.WriteStringValue(value);
    }
}
