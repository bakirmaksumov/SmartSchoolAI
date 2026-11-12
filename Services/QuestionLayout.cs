using SmartSchoolAI.Models.Dtos;

namespace SmartSchoolAI.Services;

/// <summary>
/// How a flat list of questions reads as numbered questions with parts: "1", "2", "2a", "2b".
/// A question that other questions point to through <see cref="QuestionDto.GroupId"/> is a shared stem
/// (context only, nothing to answer or mark). Labels and stems are derived on every render rather than
/// stored, so they stay right when the list is reordered; question indexes and ids are never changed.
/// </summary>
public sealed class QuestionLayout
{
    /// <summary>Display label per question index: "2" for a top-level question, "2a" for its first part.</summary>
    public string[] Labels { get; }

    /// <summary>True for a shared stem: it has parts, so it is shown for context and is not an answerable item.</summary>
    public bool[] IsStem { get; }

    /// <summary>For a part, the index of its parent question; otherwise -1.</summary>
    public int[] ParentOf { get; }

    private QuestionLayout(string[] labels, bool[] isStem, int[] parentOf)
    {
        Labels = labels;
        IsStem = isStem;
        ParentOf = parentOf;
    }

    public static QuestionLayout Compute(IReadOnlyList<QuestionDto> questions)
    {
        var n = questions.Count;
        var idToIndex = new Dictionary<string, int>();
        for (var i = 0; i < n; i++)
            if (!string.IsNullOrEmpty(questions[i].Id)) idToIndex[questions[i].Id!] = i;

        var isStem = new bool[n];
        var parentOf = new int[n];
        for (var i = 0; i < n; i++)
        {
            parentOf[i] = -1;
            var gid = questions[i].GroupId;
            if (!string.IsNullOrEmpty(gid) && idToIndex.TryGetValue(gid!, out var parent) && parent != i)
            {
                parentOf[i] = parent;
                isStem[parent] = true;
            }
        }

        var labels = new string[n];
        var topLevel = 0;
        var partCounter = new Dictionary<int, int>();
        for (var i = 0; i < n; i++)
        {
            if (parentOf[i] < 0)
            {
                labels[i] = (++topLevel).ToString();
            }
            else
            {
                var count = partCounter.TryGetValue(parentOf[i], out var c) ? c : 0;
                partCounter[parentOf[i]] = count + 1;
                // A parent listed after its part has no label yet; fall back to the position-based number.
                var parentLabel = labels[parentOf[i]] ?? (parentOf[i] + 1).ToString();
                labels[i] = parentLabel + (char)('a' + count);
            }
        }

        return new QuestionLayout(labels, isStem, parentOf);
    }
}
