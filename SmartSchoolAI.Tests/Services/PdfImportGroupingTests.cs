using System.Text.Json;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// How parts of one exam question ("3 (a)", "3 (b)") are grouped after the AI reads a paper,
/// and how the marks and drawing flag it reports are read.
/// </summary>
public class PdfImportGroupingTests
{
    private static ParsedQuestion Part(int number, string label, string? stem = null) =>
        new() { Number = number, Part = label, Title = $"part {label}", Stem = stem };

    [Fact]
    public void Parts_WithSharedText_AreGroupedWithTheirStem()
    {
        var items = PdfImportService.GroupParts(new() { Part(4, "a", "f(x) = 2x² + 3"), Part(4, "b", "f(x) = 2x² + 3") });

        Assert.All(items, p => Assert.Equal("q4", p.GroupKey));
        Assert.All(items, p => Assert.Equal("f(x) = 2x² + 3", p.Stem));
        Assert.All(items, p => Assert.Null(p.ValidationWarning));
    }

    [Fact]
    public void Parts_WithNoSharedText_AreStillGroupedWithoutAWarning()
    {
        // "3. (a) … (b) …" — the question goes straight into its parts.
        var items = PdfImportService.GroupParts(new() { Part(3, "a"), Part(3, "b") });

        Assert.All(items, p => Assert.Equal("q3", p.GroupKey));
        Assert.All(items, p => Assert.Null(p.Stem));
        Assert.All(items, p => Assert.Null(p.ValidationWarning));
    }

    [Fact]
    public void A_LonePart_IsNotGroupedAndIsFlagged()
    {
        var items = PdfImportService.GroupParts(new() { Part(2, "b", "stem") });

        Assert.Null(items[0].GroupKey);
        Assert.NotNull(items[0].ValidationWarning);
    }

    [Fact]
    public void Parts_WithRepeatedLabels_AreNotGrouped()
    {
        var items = PdfImportService.GroupParts(new() { Part(5, "a", "s"), Part(5, "a", "s") });

        Assert.All(items, p => Assert.Null(p.GroupKey));
        Assert.All(items, p => Assert.Contains("labels repeat", p.ValidationWarning));
    }

    [Theory]
    [InlineData("4", 4)]
    [InlineData("\"[3]\"", 3)]
    [InlineData("null", null)]
    [InlineData("\"none\"", null)]
    public void Marks_AreReadLeniently(string json, int? expected)
    {
        var q = JsonSerializer.Deserialize<ParsedQuestion>(
            $$"""{ "title": "t", "marks": {{json}}, "needsDrawing": "true" }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(expected, q.Marks);
        Assert.True(q.NeedsDrawing);
    }
}
