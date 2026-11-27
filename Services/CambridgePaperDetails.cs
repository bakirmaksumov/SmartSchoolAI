using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace SmartSchoolAI.Services;

/// <summary>
/// What a Cambridge paper or mark scheme says it is — syllabus code, component, variant, session and year — read
/// locally from the PDF's own text (cover and page footers), without AI. Values that cannot be read stay null and the
/// teacher is asked to check them.
/// </summary>
public sealed record PaperDetails(
    string? Code, int? Component, string? Variant, string? Session, int? Year, bool IsMarkScheme, int Pages)
{
    /// <summary>"9709/12", "9709/01" for a specimen, or what is known.</summary>
    public string PaperCode => Code is null ? "?" : Component is null ? Code
        : Variant is null ? $"{Code}/0{Component}" : $"{Code}/{Component}{Variant}";

    public string Describe() =>
        $"{PaperCode} · {(Session ?? "session ?")} {(Year?.ToString() ?? "year ?")}";

    /// <summary>True when code, component, session and year were all read (a specimen has no variant).</summary>
    public bool Complete => Code is not null && Component is not null && Session is not null && Year is not null;

    public static PaperDetails Read(byte[] pdf)
    {
        var pages = new List<string>();
        try
        {
            using var ms = new MemoryStream(pdf);
            using var doc = new PdfDocument(new PdfReader(ms));
            for (var i = 1; i <= doc.GetNumberOfPages(); i++)
            {
                try { pages.Add(PdfTextExtractor.GetTextFromPage(doc.GetPage(i), new LocationTextExtractionStrategy())); }
                catch { pages.Add(""); }
            }
        }
        catch
        {
            return new PaperDetails(null, null, null, null, null, false, 0);
        }
        return FromText(pages);
    }

    private static readonly Regex CodeRx      = new(@"(?<!\d)(9\d{3})\s*/\s*(\d)(\d)(?!\d)", RegexOptions.Compiled);
    private static readonly Regex FooterRx    = new(@"(?<!\d)(9\d{3})\s*/\s*(\d)(\d)\s*/\s*(F\s*/\s*M|M\s*/\s*J|O\s*/\s*N|SP)\s*/\s*(\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PaperRx     = new(@"\bPaper\s+(\d)\b", RegexOptions.Compiled);
    private static readonly Regex SessionRx   = new(@"\b(February\s*/\s*March|May\s*/\s*June|October\s*/\s*November)\s+(20\d{2})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SpecimenRx  = new(@"\bSPECIMEN\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FromYearRx  = new(@"for\s+examination\s+from\s+(20\d{2})", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MarkSchemeRx = new(@"\bMARK\s*SCHEME\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static PaperDetails FromText(IReadOnlyList<string> pages)
    {
        // The cover and the first footers carry everything; later pages only repeat it.
        var head = string.Join("\n", pages.Take(3));
        string? code = null, variant = null, session = null;
        int? component = null, year = null;

        var footer = FooterRx.Match(string.Join("\n", pages));
        var codeMatch = CodeRx.Match(head);
        if (!codeMatch.Success && footer.Success) codeMatch = CodeRx.Match(footer.Value);
        if (codeMatch.Success)
        {
            code = codeMatch.Groups[1].Value;
            (component, variant) = Split(codeMatch.Groups[2].Value[0], codeMatch.Groups[3].Value[0]);
        }
        var paper = PaperRx.Match(head);
        if (component is null && paper.Success) component = int.Parse(paper.Groups[1].Value);

        var s = SessionRx.Match(head);
        if (s.Success)
        {
            session = Normalise(s.Groups[1].Value);
            year = int.Parse(s.Groups[2].Value);
        }
        else if (SpecimenRx.IsMatch(head))
        {
            session = "Specimen";
            var from = FromYearRx.Match(head);
            if (from.Success) year = int.Parse(from.Groups[1].Value);
        }
        if ((session is null || year is null) && footer.Success)
        {
            session ??= footer.Groups[4].Value.Replace(" ", "").ToUpperInvariant() switch
            {
                "F/M" => "February/March", "M/J" => "May/June", "O/N" => "October/November", _ => "Specimen",
            };
            year ??= 2000 + int.Parse(footer.Groups[5].Value);
        }

        return new PaperDetails(code, component, variant, session, year, MarkSchemeRx.IsMatch(head), pages.Count);
    }

    /// <summary>"12" → Paper 1 variant 2; "01" (a specimen) → Paper 1, no variant.</summary>
    private static (int?, string?) Split(char first, char second) =>
        first == '0' ? (second - '0', null) : (first - '0', second.ToString());

    private static string Normalise(string session) => Regex.Replace(session, @"\s*/\s*", "/") switch
    {
        var x when x.StartsWith("Feb", StringComparison.OrdinalIgnoreCase) => "February/March",
        var x when x.StartsWith("May", StringComparison.OrdinalIgnoreCase) => "May/June",
        _ => "October/November",
    };

    /// <summary>
    /// How a mark scheme's details compare with the paper's: the fields that clearly differ (both read, not equal),
    /// and whether some could not be read at all. A specimen's missing variant is not a difference.
    /// </summary>
    public static (List<(string Field, string Paper, string Scheme)> Conflicts, bool Unreadable) Compare(PaperDetails paper, PaperDetails scheme)
    {
        var conflicts = new List<(string, string, string)>();
        void Check(string field, string? a, string? b)
        {
            if (a is not null && b is not null && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) conflicts.Add((field, a, b));
        }
        Check("Syllabus code", paper.Code, scheme.Code);
        Check("Component", paper.Component?.ToString(), scheme.Component?.ToString());
        Check("Variant", paper.Variant ?? (paper.Session == "Specimen" ? "—" : null), scheme.Variant ?? (scheme.Session == "Specimen" ? "—" : null));
        Check("Session", paper.Session, scheme.Session);
        Check("Year", paper.Year?.ToString(), scheme.Year?.ToString());
        return (conflicts, !scheme.Complete);
    }
}
