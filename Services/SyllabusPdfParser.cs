using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace SmartSchoolAI.Services;

/// <summary>
/// Reads a Cambridge syllabus PDF locally (no AI): the subject code and exam years from the title page, and the
/// components and topic headings from the "Subject content" section. Only headings such as "1.1 Quadratics" are
/// topics; the learning objectives listed under them ("Candidates should be able to: • …") are not.
/// </summary>
public static class SyllabusPdfParser
{
    public sealed record ParsedTopic(string Number, string Title, int Page);
    public sealed record ParsedComponent(int Number, string Title, int Page, List<ParsedTopic> Topics);
    public sealed record ParsedSyllabus(
        string? Code, string? Title, int? FirstYear, int? LastYear, string? DocumentVersion,
        List<ParsedComponent> Components, List<string> Problems);

    /// <summary>Extracts each page's text (index 0 = page 1) and parses it.</summary>
    public static ParsedSyllabus Parse(byte[] pdf)
    {
        var pages = new List<string>();
        try
        {
            using var ms = new MemoryStream(pdf);
            using var reader = new PdfReader(ms);
            using var doc = new PdfDocument(reader);
            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                try { pages.Add(PdfTextExtractor.GetTextFromPage(doc.GetPage(i), new LocationTextExtractionStrategy())); }
                catch { pages.Add(string.Empty); }
            }
        }
        catch (Exception)
        {
            return new ParsedSyllabus(null, null, null, null, null, new(), new() { "The file could not be read as a PDF." });
        }
        return ParsePages(pages);
    }

    // "1 Pure Mathematics 1 (for Paper 1)" — the heading that opens a component in Subject content.
    private static readonly Regex ComponentHeading = new(
        @"(?<![\d.])(?<n>[1-9])\s+(?<title>[A-Z][A-Za-z&'’ ]*?[A-Za-z0-9])\s*\(for (?:Paper|Component) (?<paper>[1-9])\)",
        RegexOptions.Compiled);

    // "1.1 Quadratics", possibly followed on the same extracted line by "Candidates should be able to:".
    private static readonly Regex TopicHeading = new(
        @"(?<![\w.])(?<c>[1-9])\.(?<t>\d{1,2})\s+(?<title>[A-Z][A-Za-z&'’,\- –]*?[a-z])(?=\s+Candidates should|\s*$)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex Year = new(@"\b(20\d{2})\b", RegexOptions.Compiled);

    public static ParsedSyllabus ParsePages(IReadOnlyList<string> pages)
    {
        var problems = new List<string>();
        var cover = pages.Count > 0 ? Normalise(pages[0]) : "";

        // Title page: "Cambridge International AS & A Level Mathematics 9709 … exams in 2026 and 2027 … Version 5".
        var codeMatch = Regex.Match(cover, @"\b([A-Z][A-Za-z ]+?)\s+(\d{4})\b(?!\s*(?:,|and))");
        string? code = null, title = null;
        var m = Regex.Match(cover, @"(?<title>Cambridge International[\s\S]{0,60}?Level)\s+(?<subject>[A-Z][A-Za-z ]+?)\s+(?<code>\d{4})\b");
        if (m.Success)
        {
            code = m.Groups["code"].Value;
            title = $"{Regex.Replace(m.Groups["title"].Value, @"\s+", " ")} {m.Groups["subject"].Value.Trim()}";
        }
        else if (codeMatch.Success) { code = codeMatch.Groups[2].Value; title = codeMatch.Groups[1].Value.Trim(); }

        var examSentence = Regex.Match(cover, @"(?:exams? in|examination in)[^.]*\.", RegexOptions.IgnoreCase);
        var years = Year.Matches(examSentence.Success ? examSentence.Value : cover)
            .Select(y => int.Parse(y.Value)).Where(y => code is null || y.ToString() != code).ToList();
        int? first = years.Count > 0 ? years.Min() : null, last = years.Count > 0 ? years.Max() : null;
        var docVersion = Regex.Match(cover, @"\bVersion\s+\d+\b") is { Success: true } v ? v.Value : null;

        if (code is null) problems.Add("The subject code was not found on the title page.");
        if (first is null) problems.Add("The exam years were not found on the title page.");

        // Subject content runs from its heading to "4 Details of the assessment"; the contents page lists the same
        // headings with dot leaders, and the overview page lists the topics without them — both are skipped.
        var components = new List<ParsedComponent>();
        var inSection = false;
        for (var p = 1; p < pages.Count; p++)
        {
            var text = Normalise(pages[p]);
            var lines = text.Split('\n').Select(l => l.Trim()).ToList();
            if (lines.Any(l => l.Contains("..")) && lines.Any(l => l.StartsWith("Contents"))) continue;   // contents page
            if (!inSection)
            {
                if (lines.Any(l => Regex.IsMatch(l, @"^\d?\s*Subject content$"))) inSection = true;
                else continue;
            }
            else if (lines.Any(l => Regex.IsMatch(l, @"^\d\s+Details of the assessment$"))) break;

            // Headings in reading order: a component heading opens a component, topic headings belong to it.
            var hits = ComponentHeading.Matches(text).Select(x => (x.Index, Comp: (Match?)x, Topic: (Match?)null))
                .Concat(TopicHeading.Matches(text).Select(x => (x.Index, Comp: (Match?)null, Topic: (Match?)x)))
                .OrderBy(x => x.Index);
            foreach (var hit in hits)
            {
                if (hit.Comp is { } c)
                {
                    var n = int.Parse(c.Groups["n"].Value);
                    if (components.All(k => k.Number != n))
                        components.Add(new ParsedComponent(n, c.Groups["title"].Value.Trim(), p + 1, new()));
                }
                else if (hit.Topic is { } t)
                {
                    var compNo = int.Parse(t.Groups["c"].Value);
                    var comp = components.LastOrDefault(k => k.Number == compNo);
                    var number = $"{compNo}.{int.Parse(t.Groups["t"].Value)}";
                    var topicTitle = Regex.Replace(t.Groups["title"].Value, @"\s+", " ").Trim();
                    if (comp is null || comp.Topics.Any(x => x.Number == number) || topicTitle.Split(' ').Length > 9) continue;
                    comp.Topics.Add(new ParsedTopic(number, topicTitle, p + 1));
                }
            }
        }

        if (components.Count == 0) problems.Add("No components were found in the Subject content section.");
        foreach (var c in components.Where(c => c.Topics.Count == 0))
            problems.Add($"No topics were found for component {c.Number} ({c.Title}).");
        components = components.OrderBy(c => c.Number).ToList();
        foreach (var c in components) c.Topics.Sort((a, b) => CompareNumbers(a.Number, b.Number));

        return new ParsedSyllabus(code, title, first, last, docVersion, components, problems);
    }

    private static int CompareNumbers(string a, string b)
    {
        static (int, int) Key(string s) { var p = s.Split('.'); return (int.Parse(p[0]), int.Parse(p[1])); }
        return Key(a).CompareTo(Key(b));
    }

    private static string Normalise(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ');
}
