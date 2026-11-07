using System.Text.Json;

namespace SmartSchoolAI.Services;

/// <summary>
/// Helpers for reading the image path(s) stored on a handwritten answer.
/// A submission may hold several photos (pages of one solution), stored as a
/// JSON array. Older single-photo submissions stored a plain path — both read here.
/// </summary>
public static class SubmissionMedia
{
    public static List<string> Paths(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new();

        raw = raw.Trim();
        if (raw.StartsWith("["))
        {
            try { return JsonSerializer.Deserialize<List<string>>(raw) ?? new(); }
            catch { return new(); }
        }

        return new List<string> { raw };
    }
}
