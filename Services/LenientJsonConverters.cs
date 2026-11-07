using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartSchoolAI.Services;

// Model output is only loosely typed: the same field can arrive as a number in one reply and
// as text in the next ("number": "Practice Example 1", "answerKey": 7). These converters accept
// either form so one odd value doesn't discard the entire import.

/// <summary>Reads an integer from a number, or the first run of digits in a string (0 if none).</summary>
public sealed class LenientIntConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var whole) ? whole : (int)Math.Round(reader.GetDouble());
            case JsonTokenType.String:
                var digits = new string((reader.GetString() ?? "").SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(digits, out var parsed) ? parsed : 0;
            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>Reads text from a string, number or boolean; anything structured is kept as raw JSON text.</summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => ReadText(ref reader);

    internal static string? ReadText(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();
            case JsonTokenType.True:
                return "true";
            case JsonTokenType.False:
                return "false";
            case JsonTokenType.Null:
                return null;
            default:
                // Numbers keep their literal text; arrays/objects are kept as raw JSON.
                using (var doc = JsonDocument.ParseValue(ref reader))
                    return doc.RootElement.GetRawText();
        }
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>Reads a list of strings whose items may be numbers or other scalar values.</summary>
public sealed class LenientStringListConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new List<string>();
        if (reader.TokenType == JsonTokenType.Null) return result;

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // A single value instead of an array: treat it as one option.
            var single = LenientStringConverter.ReadText(ref reader);
            if (!string.IsNullOrEmpty(single)) result.Add(single);
            return result;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var item = LenientStringConverter.ReadText(ref reader);
            if (item is not null) result.Add(item);
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

/// <summary>Reads a boolean from true/false or the strings "true"/"false"/"yes"/"no".</summary>
public sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:  return true;
            case JsonTokenType.False: return false;
            case JsonTokenType.String:
                return (reader.GetString() ?? "").Trim().ToLowerInvariant() is "true" or "yes" or "1";
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var n) && n != 0;
            default:
                reader.Skip();
                return false;
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
