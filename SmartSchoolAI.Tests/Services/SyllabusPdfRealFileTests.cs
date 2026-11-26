using SmartSchoolAI.Services;
using Xunit;
using Xunit.Abstractions;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// Opt-in: parses the real Cambridge 9709 syllabus PDFs (not stored in the repository — they are Cambridge's
/// documents). Set SMARTSCHOOL_SYLLABUS_PDF_DIR to a folder holding 9709-2026-2027.pdf and/or 9709-2023-2025.pdf.
/// </summary>
public class SyllabusPdfRealFileTests
{
    private readonly ITestOutputHelper _out;
    public SyllabusPdfRealFileTests(ITestOutputHelper output) => _out = output;

    private static string? File(string name)
    {
        var dir = Environment.GetEnvironmentVariable("SMARTSCHOOL_SYLLABUS_PDF_DIR");
        var path = dir is null ? null : Path.Combine(dir, name);
        return path is not null && System.IO.File.Exists(path) ? path : null;
    }

    [Theory]
    [InlineData("9709-2026-2027.pdf", 2026, 2027)]
    [InlineData("9709-2023-2025.pdf", 2023, 2025)]
    public void Real_9709_syllabus_gives_six_components_and_their_topic_headings(string file, int first, int last)
    {
        var path = File(file);
        if (path is null) { _out.WriteLine($"skipped: {file} not provided"); return; }

        var s = SyllabusPdfParser.Parse(System.IO.File.ReadAllBytes(path));
        _out.WriteLine($"{s.Code} · {s.Title} · {s.FirstYear}–{s.LastYear} · {s.DocumentVersion}");
        foreach (var c in s.Components)
        {
            _out.WriteLine($"{c.Number} {c.Title} (p.{c.Page}) — {c.Topics.Count} topics");
            foreach (var t in c.Topics) _out.WriteLine($"   {t.Number} {t.Title} (p.{t.Page})");
        }
        foreach (var p in s.Problems) _out.WriteLine("PROBLEM: " + p);

        Assert.Equal("9709", s.Code);
        Assert.Equal(first, s.FirstYear);
        Assert.Equal(last, s.LastYear);
        Assert.Empty(s.Problems);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, s.Components.Select(c => c.Number));
        Assert.Equal(new[] { "Pure Mathematics 1", "Pure Mathematics 2", "Pure Mathematics 3", "Mechanics",
                             "Probability & Statistics 1", "Probability & Statistics 2" }, s.Components.Select(c => c.Title));
        Assert.Equal(new[] { 8, 6, 9, 5, 5, 5 }, s.Components.Select(c => c.Topics.Count));
        Assert.Equal("Quadratics", s.Components[0].Topics[0].Title);
        Assert.Equal("Newton's laws of motion", s.Components[3].Topics[3].Title.Replace('’', '\''));
    }
}
