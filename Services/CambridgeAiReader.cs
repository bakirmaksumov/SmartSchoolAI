namespace SmartSchoolAI.Services;

/// <summary>Reads a Cambridge Question Paper or Mark Scheme PDF with a model that accepts PDF files. Returns the
/// model's JSON as text; parsing and every decision about it stay in <see cref="CambridgeImportService"/>.</summary>
public interface ICambridgeAiReader
{
    Task<string> ReadPaperAsync(byte[] pdf, string topicList, Guid importId, CancellationToken ct = default);
    Task<string> ReadMarkSchemeAsync(byte[] pdf, string partList, Guid importId, CancellationToken ct = default);
}

/// <summary>
/// The OpenRouter implementation. The model is <c>OpenRouter:CambridgeImport:Model</c>, falling back to
/// <c>OpenRouter:PdfModel</c> (the one the ordinary PDF import uses). Every call is logged with its cost by
/// <see cref="OpenRouterClient"/>; nothing of the paper goes into the log.
/// </summary>
public class OpenRouterCambridgeReader : ICambridgeAiReader
{
    private readonly OpenRouterClient _client;
    private readonly IConfiguration _config;

    public OpenRouterCambridgeReader(OpenRouterClient client, IConfiguration config)
    {
        _client = client;
        _config = config;
    }

    private string Model =>
        _config["OpenRouter:CambridgeImport:Model"] is { Length: > 0 } m ? m
        : _config["OpenRouter:PdfModel"] is { Length: > 0 } p ? p : "google/gemini-2.5-flash";

    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Clamp(_config.GetValue("OpenRouter:CambridgeImport:TimeoutSeconds", 300), 30, 900));

    public async Task<string> ReadPaperAsync(byte[] pdf, string topicList, Guid importId, CancellationToken ct = default)
    {
        var reply = await _client.CompleteAsync(Model, Messages(PaperPrompt, pdf,
                "Read every question of this Cambridge Question Paper.\n\nSyllabus topics for this component (number — title):\n" + topicList),
            new AiCallContext("cambridge-paper", CacheKey: importId.ToString()), temperature: 0.1, timeout: Timeout, cancellationToken: ct);
        return OpenRouterClient.CleanJson(reply.Content);
    }

    public async Task<string> ReadMarkSchemeAsync(byte[] pdf, string partList, Guid importId, CancellationToken ct = default)
    {
        var reply = await _client.CompleteAsync(Model, Messages(MarkSchemePrompt, pdf,
                "Read every marking row of this Cambridge Mark Scheme.\n\nThe question paper has these subparts (full numbers, marks):\n" + partList),
            new AiCallContext("cambridge-mark-scheme", CacheKey: importId.ToString()), temperature: 0.1, timeout: Timeout, cancellationToken: ct);
        return OpenRouterClient.CleanJson(reply.Content);
    }

    private static object[] Messages(string system, byte[] pdf, string instruction) => new object[]
    {
        new { role = "system", content = system },
        new
        {
            role = "user",
            content = new object[]
            {
                new { type = "image_url", image_url = new { url = "data:application/pdf;base64," + Convert.ToBase64String(pdf) } },
                new { type = "text", text = instruction },
            }
        }
    };

    // The prompts are the contract with the mock AI used by the browser tests (E2E/scripts/mock-ai.mjs recognises
    // them by "Cambridge Question Paper reader" and "Cambridge Mark Scheme reader").
    internal const string PaperPrompt = """
        You are a Cambridge Question Paper reader for Cambridge International AS & A Level Mathematics (9709).
        Read the attached question paper and return its questions as JSON only (no markdown, no comments).

        Rules:
        - Return every numbered question. Break it into its answerable subparts down to the deepest level:
          "(a)", "(b)(i)", "(b)(ii)". A question without parts has one subpart with label "".
        - "stem": the shared text printed before the first part, which every part depends on (the setup, the
          definitions, "The diagram shows …"). null when there is none.
        - "text": ONLY the subpart's own wording, without its label. If text is printed between parts (e.g. "It is
          now given that k = −1.") or a part (b) has its own lead-in before (i) and (ii), put that text at the start of
          "text" of EVERY later subpart that depends on it. Never drop it.
        - Write all mathematics as LaTeX inside $...$ (fractions \frac{a}{b}, powers x^{2}, roots \sqrt{x},
          inverse functions f^{-1}(x), sets \mathbb{R}). Keep every sign and symbol exactly.
        - "marks": the number in square brackets after the subpart, e.g. [4] → 4; null when none is printed.
        - "page": the 1-based PDF page where the subpart is printed.
        - "figure": when the question has a diagram, graph or table printed on the paper, give its 1-based "page" and
          "box" [left, top, right, bottom] as fractions (0–1) of the page width and height, generously including
          labels, and a one-sentence "description". Otherwise null. Parts that refer to the figure set "usesFigure".
        - "kind": "OpenText" for show that / prove / sketch / describe / explain; otherwise "ShortAnswer".
          "needsDrawing": true when the answer is a sketch or drawing.
        - "topic": the number of the ONE syllabus topic (from the list given) this subpart mainly assesses, and
          "topicConfidence": "high" or "low". Use null when no listed topic fits.

        JSON:
        {"questions":[{"number":1,"stem":null,"figure":null,
          "parts":[{"label":"(a)","text":"...","marks":2,"page":2,"usesFigure":false,"kind":"ShortAnswer",
                    "needsDrawing":false,"topic":"1.2","topicConfidence":"high"}]}]}
        """;

    internal const string MarkSchemePrompt = """
        You are a Cambridge Mark Scheme reader for Cambridge International AS & A Level Mathematics (9709).
        Read the question-by-question table of the attached mark scheme (ignore the general marking principles and
        abbreviation pages) and return its rows as JSON only (no markdown, no comments).

        Rules:
        - One object per row of the table, in order. "question" is the question label exactly as printed in the
          Question column, e.g. "10(b)(i)". When a row has an empty Question column it continues the row above: repeat
          that label. A table that continues on the next page keeps its label.
        - "appliesTo": the full subpart numbers this row marks, e.g. ["10(b)(i)"]. When one row covers several
          subparts (e.g. "5(a)(b)") list them all.
        - "answer": the Answer column; "codes": the mark codes as printed ("M1", "A1", "B1", "DM1", "M1*", "A1FT",
          "B2, 1, 0"); "marks": the number in the Marks column for that row (0 when empty); "guidance": the Guidance
          column. Write all mathematics as LaTeX inside $...$; keep "AWRT", "OE", "CAO", "FT", "SOI", domains,
          units and rounding instructions exactly.
        - "alternative": null for the main method; for rows under "Alternative method …" the heading text, e.g.
          "Alternative method 1". Alternative methods are never added to the main method.
        - "final": true on the row that gives the subpart's final answer.
        - "unreadable": true when a formula or value in the row cannot be read reliably.
        - "page": the 1-based PDF page of the row.

        JSON:
        {"rows":[{"question":"1(a)","appliesTo":["1(a)"],"answer":"...","codes":"B1","marks":1,"guidance":"...",
                  "alternative":null,"final":true,"unreadable":false,"page":4}]}
        """;
}
