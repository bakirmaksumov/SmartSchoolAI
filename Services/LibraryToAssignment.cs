using System.Text.Json;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// Adds Content Library questions to an assignment's question list — the one place both "Add to assignment" in the
/// library and "Add from library" in the assignment editor go through, so a question arrives the same way.
/// An exam-paper part (one with a shared stem or figure) is placed under a shared question that carries the stem and
/// the figure; further parts of the same source question join that shared question instead of repeating it.
/// </summary>
public static class LibraryToAssignment
{
    /// <summary>Adds the questions not already in <paramref name="list"/>; returns how many were added.</summary>
    public static async Task<int> AddAsync(IWebHostEnvironment env, Guid assignmentId, List<JsonElement> list, IEnumerable<Question> questions)
    {
        var existing = list.Select(e => e.TryGetProperty("id", out var p) ? p.ToString() : null).Where(x => x is not null).ToHashSet();
        var added = 0;
        foreach (var q in questions)
        {
            if (!existing.Add(q.Id.ToString())) continue;

            string? groupId = null;
            if (!string.IsNullOrWhiteSpace(q.Stem) || q.ImagePath is not null)
            {
                var groupKey = q.SourceGroupKey ?? "library:" + q.Id;
                groupId = FindGroup(list, groupKey);
                if (groupId is null)
                {
                    groupId = Guid.NewGuid().ToString();
                    string? image = null;
                    if (LibraryFileStorage.ReadLibraryFigure(env, q.ImagePath) is { } bytes)
                        image = await QuestionAttachmentStorage.SaveAsync(env, bytes, ".png", assignmentId, groupId, "diagram");
                    list.Add(Element(new
                    {
                        id = groupId, title = q.Stem ?? "", type = 0, difficulty = q.Difficulty.ToString(),
                        answerKey = "", options = Array.Empty<string>(), imagePath = image, sourceGroup = groupKey,
                    }));
                }
            }

            var part = Element(new
            {
                id          = q.Id,
                groupId,
                title       = q.Title,
                type        = (int)q.Type,
                difficulty  = q.Difficulty.ToString(),
                answerKey   = q.AnswerKey,
                options     = SafeOptions(q.Options),
                visualState = q.VisualState,
                marks       = q.Marks,
                markScheme  = q.MarkScheme,
                requireWork = q.SourceKey is not null && !q.RequireDrawing,   // exam questions are marked on the working
                requireDrawing = q.RequireDrawing,          // answered on the sketch pad over the shared figure
                syllabusTopicId = q.SyllabusTopicId,        // the teacher's "Aligned to Cambridge" link travels with the question
            });
            // Keep a group's parts together, right after the shared question and its other parts.
            var at = groupId is null ? -1 : list.FindLastIndex(e => Id(e) == groupId || GroupOf(e) == groupId);
            if (at >= 0) list.Insert(at + 1, part); else list.Add(part);
            added++;
        }
        return added;
    }

    private static string? FindGroup(List<JsonElement> list, string key) =>
        list.Where(e => e.TryGetProperty("sourceGroup", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == key)
            .Select(Id).FirstOrDefault();

    private static string? Id(JsonElement e) => e.TryGetProperty("id", out var p) ? p.ToString() : null;
    private static string? GroupOf(JsonElement e) =>
        e.TryGetProperty("groupId", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string> SafeOptions(string? options)
    {
        try { return JsonSerializer.Deserialize<List<string>>(options ?? "[]") ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static JsonElement Element(object o)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(o));
        return doc.RootElement.Clone();
    }
}
