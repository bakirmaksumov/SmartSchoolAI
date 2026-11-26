using System.Text.Json;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Tests.Services;

public class JsonSanitizerTests
{
    [Fact]
    public void FixInvalidBackslashes_NewlineOutsideMath_StaysARealNewline()
    {
        var json = """{"title":"line one.\nline two."}""";

        var fixedJson = JsonSanitizer.FixInvalidBackslashes(json);
        var doc = JsonDocument.Parse(fixedJson);

        Assert.Equal("line one.\nline two.", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void FixInvalidBackslashes_LatexCommandInsideMath_SurvivesAsLiteralBackslash()
    {
        var json = """{"title":"solve $x \neq 0$ for x"}""";

        var fixedJson = JsonSanitizer.FixInvalidBackslashes(json);
        var doc = JsonDocument.Parse(fixedJson);

        Assert.Equal(@"solve $x \neq 0$ for x", doc.RootElement.GetProperty("title").GetString());
    }

    /// <summary>
    /// Regression test for the reported bug: on the live database, a question
    /// with an unpaired `$` desynced the math-span toggle for every question
    /// after it in the same AI response, turning their real newlines into a
    /// literal two-character "\n" visible in the rendered question text.
    /// </summary>
    [Fact]
    public void FixInvalidBackslashes_UnpairedDollarInOneString_DoesNotCorruptTheNextString()
    {
        // The first question has a stray, unpaired '$' (malformed LaTeX from
        // the model) — this alone is the trigger; nothing about the SECOND
        // question is malformed.
        var json = """
            [
              {"title":"the price is $5 today"},
              {"title":"(a) Sketch the graph.\n(b) Hence solve the inequality."}
            ]
            """;

        var fixedJson = JsonSanitizer.FixInvalidBackslashes(json);
        var doc = JsonDocument.Parse(fixedJson);

        var secondTitle = doc.RootElement[1].GetProperty("title").GetString();

        // Before the fix: the unpaired '$' in question 1 flips `inMath` to
        // true for the rest of the document, so the '\n' in question 2 is
        // treated as "inside math" and gets its backslash doubled — which
        // decodes to the literal text "\n" instead of a line break.
        Assert.Equal("(a) Sketch the graph.\n(b) Hence solve the inequality.", secondTitle);
        Assert.DoesNotContain(@"\n", secondTitle); // no literal backslash-n left in the decoded text
    }

    [Fact]
    public void FixInvalidBackslashes_EmptyOrNull_ReturnsUnchanged()
    {
        Assert.Equal(string.Empty, JsonSanitizer.FixInvalidBackslashes(string.Empty));
        Assert.Null(JsonSanitizer.FixInvalidBackslashes(null!));
    }
}
