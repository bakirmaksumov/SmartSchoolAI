using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// The decisions of a Cambridge paper import, kept free of the database and the AI so they can be tested on their
/// own: reading the model's JSON, numbering subparts, and matching mark scheme rows to subparts.
/// </summary>
public static class CambridgeImportLogic
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ── What the model returns ───────────────────────────────────────────────────────────────────────────────────
    public sealed class PaperReading { public List<PaperQuestion> Questions { get; set; } = new(); }
    public sealed class PaperQuestion
    {
        public int Number { get; set; }
        public string? Stem { get; set; }
        public PaperFigure? Figure { get; set; }
        public List<PaperPart> Parts { get; set; } = new();
    }
    public sealed class PaperFigure
    {
        public int Page { get; set; }
        public double[]? Box { get; set; }
        public string? Description { get; set; }
    }
    public sealed class PaperPart
    {
        public string? Label { get; set; }
        public string Text { get; set; } = "";
        public int? Marks { get; set; }
        public int? Page { get; set; }
        public bool UsesFigure { get; set; }
        public string? Kind { get; set; }
        public bool NeedsDrawing { get; set; }
        public string? Topic { get; set; }
        public string? TopicConfidence { get; set; }
    }
    public sealed class SchemeReading { public List<SchemeRow> Rows { get; set; } = new(); }
    public sealed class SchemeRow
    {
        public string Question { get; set; } = "";
        public List<string>? AppliesTo { get; set; }
        public string? Answer { get; set; }
        public string? Codes { get; set; }
        public int Marks { get; set; }
        public string? Guidance { get; set; }
        public string? Alternative { get; set; }
        public bool Final { get; set; }
        public bool Unreadable { get; set; }
        public int? Page { get; set; }
    }

    public static PaperReading ParsePaper(string json)
    {
        var r = JsonSerializer.Deserialize<PaperReading>(json, Json) ?? throw new JsonException("Empty reading.");
        if (r.Questions.Count == 0) throw new JsonException("No questions were found in the paper.");
        return r;
    }

    public static SchemeReading ParseScheme(string json)
    {
        var r = JsonSerializer.Deserialize<SchemeReading>(json, Json) ?? throw new JsonException("Empty reading.");
        if (r.Rows.Count == 0) throw new JsonException("No marking rows were found in the mark scheme.");
        return r;
    }

    // ── Numbering ────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly Regex Roman = new(@"^(i|ii|iii|iv|v|vi|vii|viii|ix|x)$", RegexOptions.Compiled);

    /// <summary>The full normalised number of a subpart: "10", "10(b)", "10(b)(ii)". Accepts "10 (b) (ii)",
    /// "10b(ii)", "10bii", "Q10(b)(ii)".</summary>
    public static string Normalise(string label)
    {
        var s = Regex.Replace(label ?? "", @"\s+", "").ToLowerInvariant().TrimStart('q');
        var m = Regex.Match(s, @"^(\d+)(.*)$");
        if (!m.Success) return s;
        return m.Groups[1].Value + Suffix(m.Groups[2].Value);
    }

    /// <summary>"(b)(ii)", "b(ii)", "bii", "(a)" → "(b)(ii)", "(a)"; "" → "".</summary>
    public static string Suffix(string label)
    {
        var s = Regex.Replace(label ?? "", @"\s+", "").ToLowerInvariant();
        var tokens = new List<string>();
        foreach (Match m in Regex.Matches(s, @"\(([a-z]+)\)|([a-z]+)"))
        {
            var t = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (m.Groups[1].Success || t.Length == 1 || Roman.IsMatch(t)) { tokens.Add(t); continue; }
            // Unbracketed run such as "bii": a part letter followed by a roman numeral.
            if (Roman.IsMatch(t[1..])) { tokens.Add(t[..1]); tokens.Add(t[1..]); }
            else tokens.Add(t);
        }
        return string.Concat(tokens.Select(t => $"({t})"));
    }

    // ── Question Paper → subparts ────────────────────────────────────────────────────────────────────────────────
    /// <summary>The subparts of a reading, in paper order, with their shared text and figure on each, and the
    /// suggested topic (a topic id from the published syllabus version) when the model was confident.</summary>
    public static List<CambridgeImportPart> BuildParts(PaperReading reading, IReadOnlyDictionary<string, Guid> topicsByNumber)
    {
        var parts = new List<CambridgeImportPart>();
        var order = 0;
        foreach (var q in reading.Questions.Where(q => q.Number > 0).OrderBy(q => q.Number))
        {
            var items = q.Parts.Count > 0 ? q.Parts : new List<PaperPart> { new() { Label = "", Text = q.Stem ?? "" } };
            var onlyOne = items.Count == 1 && string.IsNullOrWhiteSpace(items[0].Label);
            foreach (var p in items)
            {
                var number = q.Number + Suffix(p.Label ?? "");
                if (parts.Any(x => x.Number == number)) continue;   // a repeated label is read once
                var figure = q.Figure is { Page: > 0, Box.Length: 4 } f ? f : null;
                var format = AnswerFormat(p.Text, p.NeedsDrawing);
                Guid? topic = null;
                if (p.Topic is { } tn && topicsByNumber.TryGetValue(tn.Trim(), out var tid)
                    && !string.Equals(p.TopicConfidence, "low", StringComparison.OrdinalIgnoreCase))
                    topic = tid;
                parts.Add(new CambridgeImportPart
                {
                    QuestionNumber = q.Number,
                    Number = number,
                    Order = ++order,
                    Stem = onlyOne ? null : Blank(q.Stem),
                    Text = (onlyOne && string.IsNullOrWhiteSpace(p.Text) ? q.Stem : p.Text)?.Trim() ?? "",
                    Marks = p.Marks is > 0 ? p.Marks : null,
                    PaperPage = p.Page ?? figure?.Page,
                    Kind = string.Equals(p.Kind, "OpenText", StringComparison.OrdinalIgnoreCase) ? "OpenText" : "ShortAnswer",
                    NeedsDrawing = format.Drawing,
                    FormatQuestion = format.Question,
                    // The figure belongs to the question; every part of it keeps access to it.
                    FigureKey = figure is null ? null : $"q{q.Number}",
                    FigureBox = figure is null ? null : JsonSerializer.Serialize(new { page = figure.Page, box = figure.Box }),
                    FigureDescription = figure?.Description,
                    SyllabusTopicId = topic,
                    TopicSource = topic is null ? "none" : "suggested",
                    Status = ImportPartStatus.Recognised,
                });
            }
        }
        // A lead-in the model returned as its own part ("(b) It is given that …" before "(b)(i)" and "(b)(ii)") is not
        // answerable: it carries no marks and has subparts. Its text goes to the front of each subpart instead.
        foreach (var lead in parts.Where(p => p.Marks is null && parts.Any(c => c.Number.StartsWith(p.Number + "("))).ToList())
        {
            foreach (var child in parts.Where(c => c.Number.StartsWith(lead.Number + "(")))
                if (!string.IsNullOrWhiteSpace(lead.Text) && !child.Text.Contains(lead.Text.Trim(), StringComparison.Ordinal))
                    child.Text = lead.Text.Trim() + " " + child.Text;
            parts.Remove(lead);
        }
        var n = 0;
        foreach (var p in parts) p.Order = ++n;
        return parts;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ── Mark Scheme → subparts ───────────────────────────────────────────────────────────────────────────────────
    public sealed record PartMatch(
        string Number, ImportPartStatus Status, string? Reason, string? AnswerKey, string? Criteria, int? SchemeMarks, string? Pages);

    /// <summary>
    /// Matches each subpart to the rows that mark it, by its full normalised number — never by position, so page and
    /// row order do not matter. Rows continuing on the next page share the label and are joined. A row covering
    /// several subparts is used for each. Anything uncertain becomes Needs review with the reason.
    /// </summary>
    public static (List<PartMatch> Matches, List<string> UnusedRows) Match(
        IReadOnlyList<(string Number, int? Marks)> parts, SchemeReading scheme)
    {
        var rows = scheme.Rows.Select(r => (Row: r, Targets: Targets(r))).ToList();
        var numbers = parts.Select(p => p.Number).ToHashSet();
        var matches = new List<PartMatch>();

        foreach (var (number, marks) in parts)
        {
            var exact = rows.Where(r => r.Targets.Contains(number)).ToList();
            var reasons = new List<string>();
            if (exact.Count == 0)
            {
                // A row labelled with a parent ("10(b)") or a child ("10(b)(i)" for a paper part "10(b)") is a candidate
                // for more than one subpart: the teacher decides.
                var near = rows.Where(r => !r.Targets.Any(numbers.Contains) && r.Targets.Any(t => Related(t, number))).ToList();
                var nearTargets = near.SelectMany(r => r.Targets).Distinct().ToList();
                var candidates = parts.Select(p => p.Number).Where(n => nearTargets.Any(t => Related(t, n))).ToList();
                reasons.Add(near.Count == 0
                    ? $"No mark scheme row for {number}."
                    : $"The mark scheme row {string.Join(", ", near.Select(r => r.Row.Question).Distinct())} could belong to {string.Join(" or ", candidates)}.");
                matches.Add(new PartMatch(number, ImportPartStatus.NeedsReview, string.Join(" ", reasons), null, null, null, null));
                continue;
            }

            var main = exact.Where(r => !IsAlternative(r.Row)).Select(r => r.Row).ToList();
            var alternatives = exact.Where(r => IsAlternative(r.Row)).Select(r => r.Row)
                                    .GroupBy(r => string.IsNullOrWhiteSpace(r.Alternative) ? "Alternative method" : r.Alternative!.Trim()).ToList();
            var shared = exact.Where(r => r.Targets.Count > 1).ToList();
            var schemeMarks = main.Sum(r => r.Marks);

            // The same answer reached by two methods is one answer; distinct final answers of the main method are
            // separate results the question asks for ("the gradients of CE and DE") and all belong in the key.
            var final = main.Where(r => r.Final && !string.IsNullOrWhiteSpace(r.Answer)).Select(r => r.Answer!.Trim()).DistinctBy(Canon).ToList();
            var required = RequiredResults(main, final);
            string? key = final.Count == 1 ? final[0]
                : final.Count > 1 ? string.Join("; ", final)
                : required.Count > 1 ? string.Join("; ", required)
                : main.Count(r => !string.IsNullOrWhiteSpace(r.Answer)) == 1 ? main.First(r => !string.IsNullOrWhiteSpace(r.Answer)).Answer!.Trim()
                : null;
            var missing = key is null ? new List<string>() : required.Where(r => !KeyHas(key, r)).ToList();

            if (shared.Count > 0)
                reasons.Add($"One mark scheme row covers {string.Join(" and ", shared.SelectMany(r => r.Targets).Distinct())}; check how its marks split.");
            if (marks is > 0 && main.Count > 0 && schemeMarks != marks)
                reasons.Add($"Marks differ: paper [{marks}], mark scheme {schemeMarks}.");
            if (main.Count == 0)
                reasons.Add("Only alternative methods were found; choose the method to use.");
            foreach (var r in exact.Where(r => r.Row.Unreadable).Select(r => r.Row))
                reasons.Add($"A formula on mark scheme p.{r.Page?.ToString() ?? "?"} could not be read.");
            if (missing.Count > 0)
                reasons.Add($"Incomplete answer key: the mark scheme asks for {required.Count} separate results " +
                            $"({string.Join("; ", required)}), but the key has only {key}. Missing: {string.Join("; ", missing)}.");
            if (key is null)
                reasons.Add("The mark scheme gives no single final answer; decide the key from the criteria.");

            var pages = exact.Select(r => r.Row.Page).Where(p => p is > 0).Select(p => p!.Value).Distinct().OrderBy(p => p).ToList();
            matches.Add(new PartMatch(number,
                reasons.Count == 0 ? ImportPartStatus.Matched : ImportPartStatus.NeedsReview,
                reasons.Count == 0 ? null : string.Join(" ", reasons.Distinct()),
                key, Criteria(main, alternatives, shared.Count > 0 ? shared.SelectMany(r => r.Targets).Distinct().Where(t => t != number).ToList() : null),
                main.Count > 0 ? schemeMarks : null,
                pages.Count == 0 ? null : pages.Count == 1 ? pages[0].ToString() : $"{pages[0]}–{pages[^1]}"));
        }

        var unused = rows.Where(r => !r.Targets.Any(numbers.Contains)).Select(r => r.Row.Question).Distinct().ToList();
        return (matches, unused);
    }

    // ── Required results ─────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>A row of another method: under an "Alternative method" heading, or printed as "OR …".</summary>
    private static bool IsAlternative(SchemeRow r) =>
        !string.IsNullOrWhiteSpace(r.Alternative) || Regex.IsMatch(r.Answer ?? "", @"^\s*(OR\b|Alt(ernative)?\b)", RegexOptions.IgnoreCase);

    /// <summary>
    /// The separate results a subpart's main method requires, when it requires more than one: its distinct final
    /// answers, or — for a subpart marked by independent B marks only (no method marks, so no row is a step towards
    /// another) — each answered row, as in "B1 gradient of CE = 2.5", "B1 gradient of DE = 2.1".
    /// </summary>
    private static List<string> RequiredResults(List<SchemeRow> main, List<string> finals)
    {
        var independent = main.Count >= 2 && main.All(r => IsIndependentMark(r.Codes))
            ? main.Where(r => !string.IsNullOrWhiteSpace(r.Answer)).Select(r => r.Answer!.Trim()).DistinctBy(Canon).ToList()
            : new List<string>();
        var list = independent.Count > finals.Count ? independent : finals;
        return list.Count > 1 ? list : new List<string>();
    }

    private static bool IsIndependentMark(string? codes)
    {
        var tokens = Regex.Split(codes ?? "", @"[\s,;]+").Where(t => t.Length > 0).ToList();
        return tokens.Count > 0 && tokens.All(t => Regex.IsMatch(t, @"^B\d+(FT)?$", RegexOptions.IgnoreCase));
    }

    /// <summary>The key states this result: the whole result, or its value after "=" (at least two characters and
    /// not inside a longer number, so "2" is not found in "2.5").</summary>
    private static bool KeyHas(string key, string result)
    {
        var k = Canon(key);
        var r = Canon(result);
        if (k.Contains(r)) return true;
        var eq = r.LastIndexOf('=');
        var value = eq >= 0 ? r[(eq + 1)..] : "";
        return value.Length >= 2 && Regex.IsMatch(k, $@"(^|[^0-9.]){Regex.Escape(value)}($|[^0-9])");
    }

    /// <summary>Lower case, no spaces and no $ delimiters, for comparing answers.</summary>
    private static string Canon(string s) => Regex.Replace(s, @"[\s$]+", "").ToLowerInvariant();

    // ── Answer format ────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>Starts the review reason while the answer format still needs the teacher's choice.</summary>
    public const string FormatReasonPrefix = "Answer format: ";

    // An instruction to draw: the verb opens the sentence or a clause ("Sketch …", "Hence draw …", "On the diagram,
    // sketch …"), never a noun describing the paper's own figure ("The diagram shows a sketch of …").
    private static readonly Regex DrawTask = new(
        @"(?:^|[.;:!?]\s+|,\s*|\b(?:and|hence|then|also)\s+)(sketch|draw|plot|shade|construct|complete\s+the\s+(?:graph|diagram|sketch|grid))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public sealed record FormatCheck(bool Drawing, string? Question);

    /// <summary>
    /// Whether the student answers a subpart by drawing (the existing sketch pad over the question's figure) or in
    /// writing. A figure printed in the question is not a drawing task. When the wording and the reader disagree,
    /// the format is left for the teacher to choose (<see cref="FormatCheck.Question"/>), with drawing proposed.
    /// </summary>
    public static FormatCheck AnswerFormat(string? text, bool readAsDrawing)
    {
        var task = DrawTask.Match(text ?? "");
        if (task.Success == readAsDrawing) return new FormatCheck(readAsDrawing, null);
        return task.Success
            ? new FormatCheck(true, $"{FormatReasonPrefix}the part says “{task.Groups[1].Value} …” but was read as a written answer. Choose a drawn or a written answer.")
            : new FormatCheck(true, $"{FormatReasonPrefix}the part was read as a drawn answer, but its text does not ask the student to draw — the figure may only belong to the question. Choose a drawn or a written answer.");
    }

    /// <summary>One number is the other's parent ("10(b)" and "10(b)(i)").</summary>
    private static bool Related(string a, string b) => a.StartsWith(b + "(") || b.StartsWith(a + "(");

    private static List<string> Targets(SchemeRow r)
    {
        var list = (r.AppliesTo is { Count: > 0 } a ? a : new List<string> { r.Question })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(Normalise).Distinct().ToList();
        return list;
    }

    /// <summary>The criteria as a teacher and the marker read them: one line per row with its mark codes, then each
    /// alternative method on its own (never added to the main method).</summary>
    public static string Criteria(List<SchemeRow> main, IEnumerable<IGrouping<string, SchemeRow>> alternatives, List<string>? sharedWith)
    {
        var sb = new StringBuilder();
        void Line(SchemeRow r)
        {
            var codes = string.IsNullOrWhiteSpace(r.Codes) ? (r.Marks > 0 ? $"[{r.Marks}]" : "") : r.Codes!.Trim();
            sb.Append(codes.Length > 0 ? codes + "  " : "").Append(r.Answer?.Trim());
            if (!string.IsNullOrWhiteSpace(r.Guidance)) sb.Append("  — ").Append(r.Guidance!.Trim());
            sb.Append('\n');
        }
        foreach (var r in main) Line(r);
        foreach (var g in alternatives)
        {
            sb.Append('\n').Append(g.Key).Append(" (instead of the method above):\n");
            foreach (var r in g) Line(r);
        }
        if (sharedWith is { Count: > 0 })
            sb.Append("\nThis row is shared with ").Append(string.Join(", ", sharedWith)).Append(".\n");
        return sb.ToString().Trim();
    }
}
