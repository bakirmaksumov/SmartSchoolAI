using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// A teacher's import of a Cambridge 9709 paper, step by step: Question Paper → Mark Scheme → reviewed answer keys →
/// saved to the teacher's own Content Library. Every stage's result is stored, so reopening a page or changing a topic
/// never reads a document again; a failed reading can be retried on its own. Drafts belong to their owner only.
/// </summary>
public class CambridgeImportService
{
    private readonly AppDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly ICambridgeAiReader _reader;
    private readonly ILogger<CambridgeImportService> _logger;

    /// <summary>A reading still "running" after this long was interrupted (e.g. the app restarted) and can be retried.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public CambridgeImportService(AppDbContext db, IWebHostEnvironment env, ICambridgeAiReader reader, ILogger<CambridgeImportService> logger)
    {
        _db = db;
        _env = env;
        _reader = reader;
        _logger = logger;
    }

    // ── Loading (owner only) ─────────────────────────────────────────────────────────────────────────────────────
    public async Task<CambridgeImport?> LoadAsync(Guid id, Guid ownerId)
    {
        var import = await _db.CambridgeImports
            .Include(i => i.Parts.OrderBy(p => p.Order)).ThenInclude(p => p.SyllabusTopic!).ThenInclude(t => t.Component)
            .AsSplitQuery()
            .FirstOrDefaultAsync(i => i.Id == id && i.OwnerId == ownerId);
        if (import is null) return null;
        import.Parts = import.Parts.OrderBy(p => p.Order).ToList();
        if (MarkInterrupted(import)) await _db.SaveChangesAsync();
        return import;
    }

    private static bool MarkInterrupted(CambridgeImport i)
    {
        var stale = i.StageStartedAt is { } t && DateTime.UtcNow - t > StaleAfter;
        var changed = false;
        if (i.PaperStage == ImportStage.Running && stale)
        { i.PaperStage = ImportStage.Failed; i.PaperError = "The reading was interrupted. Your draft is saved — try again."; changed = true; }
        if (i.MarkSchemeStage == ImportStage.Running && stale)
        { i.MarkSchemeStage = ImportStage.Failed; i.MarkSchemeError = "The reading was interrupted. Your draft is saved — try again."; changed = true; }
        if (i.TopicStage == ImportStage.Running && stale)
        { i.TopicStage = ImportStage.Failed; i.TopicError = "The topic suggestion was interrupted — try again."; changed = true; }
        return changed;
    }

    /// <summary>The owner's unfinished imports, newest first: something read but not everything saved.</summary>
    public Task<List<CambridgeImport>> DraftsAsync(Guid ownerId) =>
        _db.CambridgeImports.Include(i => i.Parts)
            .Where(i => i.OwnerId == ownerId && (!i.Parts.Any() || i.Parts.Any(p => p.Status != ImportPartStatus.Saved)))
            .OrderByDescending(i => i.UpdatedAt ?? i.CreatedAt).AsSplitQuery().ToListAsync();

    // ── Step 1: Question Paper ───────────────────────────────────────────────────────────────────────────────────
    public sealed record StartResult(CambridgeImport? Import, bool Existing, string? Error);

    /// <summary>Starts an import from a Question Paper PDF. The same file again (same teacher) opens that import.</summary>
    public async Task<StartResult> StartAsync(Guid ownerId, string ownerEmail, byte[] pdf, string fileName)
    {
        var sha = Sha(pdf);
        var existing = await _db.CambridgeImports.FirstOrDefaultAsync(i => i.OwnerId == ownerId && i.PaperSha256 == sha);
        if (existing is not null) return new StartResult(existing, true, null);

        var details = PaperDetails.Read(pdf);
        if (details.Pages == 0) return new StartResult(null, false, "This file could not be opened as a PDF.");
        if (details.IsMarkScheme) return new StartResult(null, false, "This looks like a Mark Scheme. Start with the Question Paper; the mark scheme comes in step 2.");
        if (details.Code is not null && details.Code != "9709")
            return new StartResult(null, false, $"This is a {details.Code} paper. Only Cambridge International AS & A Level Mathematics (9709) papers can be imported here.");

        var import = new CambridgeImport
        {
            OwnerId = ownerId, OwnerEmail = ownerEmail,
            SyllabusCode = details.Code ?? "9709", Component = details.Component, Variant = details.Variant,
            Session = details.Session, Year = details.Year,
            PaperFileName = Path.GetFileName(fileName), PaperSha256 = sha, PaperPages = details.Pages,
        };
        import.SyllabusVersionId = await VersionForAsync(import);
        _db.CambridgeImports.Add(import);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // The same file uploaded twice at once: one import wins; open it.
            _db.ChangeTracker.Clear();
            var winner = await _db.CambridgeImports.FirstOrDefaultAsync(i => i.OwnerId == ownerId && i.PaperSha256 == sha);
            if (winner is null) throw;
            return new StartResult(winner, true, null);
        }
        await LibraryFileStorage.SaveImportPdfAsync(_env, import.Id, "paper", pdf);
        return new StartResult(import, false, null);
    }

    /// <summary>
    /// The published syllabus edition whose topics the paper's parts are linked to: the one covering the paper's year
    /// and having topics for its component; otherwise the nearest published edition of the same syllabus with topics
    /// for that component (a past paper practised under the current syllabus). The paper keeps its own year. None
    /// while the year or the component is unknown.
    /// </summary>
    public async Task<Guid?> VersionForAsync(CambridgeImport i)
    {
        if (i.Year is not { } year || i.Component is not { } component) return null;
        var usable = await _db.SyllabusVersions
            .Where(v => v.Status == SyllabusVersionStatus.Published && v.Program.Code == i.SyllabusCode
                        && v.Components.Any(c => c.Number == component && c.Topics.Any()))
            .Select(v => new { v.Id, v.FirstExamYear, v.LastExamYear }).ToListAsync();
        if (usable.Count == 0) return null;
        return usable
            .OrderBy(v => year < v.FirstExamYear ? v.FirstExamYear - year : year > v.LastExamYear ? year - v.LastExamYear : 0)
            .ThenByDescending(v => v.FirstExamYear)
            .First().Id;
    }

    /// <summary>Where the topics come from, for the page: whether there are any, from which edition, and if it is
    /// the paper's own or the nearest one.</summary>
    public sealed record TopicBasis(string State, string? Years, bool Exact, string? ComponentTitle, int TopicCount)
    {
        /// <summary>"details": the year or component is missing; "none": no published topics for it; "ok".</summary>
        public bool Ok => State == "ok";
    }

    public async Task<TopicBasis> TopicBasisAsync(CambridgeImport i)
    {
        if (i.Year is null || i.Component is null) return new TopicBasis("details", null, false, null, 0);
        if (i.SyllabusVersionId is not { } vid) return new TopicBasis("none", null, false, null, 0);
        var v = await _db.SyllabusVersions.Where(x => x.Id == vid)
            .Select(x => new
            {
                x.FirstExamYear, x.LastExamYear,
                Component = x.Components.Where(c => c.Number == i.Component).Select(c => new { c.Title, Count = c.Topics.Count }).FirstOrDefault(),
            }).FirstOrDefaultAsync();
        if (v?.Component is null || v.Component.Count == 0) return new TopicBasis("none", null, false, null, 0);
        var years = v.FirstExamYear == v.LastExamYear ? $"{v.FirstExamYear}" : $"{v.FirstExamYear}–{v.LastExamYear}";
        return new TopicBasis("ok", years, v.FirstExamYear <= i.Year && i.Year <= v.LastExamYear, v.Component.Title, v.Component.Count);
    }

    /// <summary>The component's topics of the import's syllabus edition, and the list given to the model.</summary>
    public async Task<(List<CambridgeImportLogic.TopicRef> Topics, string Text)> TopicsAsync(CambridgeImport i)
    {
        if (i.SyllabusVersionId is null || i.Component is null) return (new(), "(no published syllabus topics for this paper — return \"topic\": null)");
        var topics = await _db.SyllabusTopics
            .Where(t => t.Component.VersionId == i.SyllabusVersionId && t.Component.Number == i.Component)
            .OrderBy(t => t.Order).Select(t => new CambridgeImportLogic.TopicRef(t.Id, t.Number, t.Title)).ToListAsync();
        return (topics, topics.Count == 0 ? "(none — return \"topic\": null)" : string.Join("\n", topics.Select(t => $"{t.Number} {t.Title}")));
    }

    /// <summary>
    /// Brings an existing draft up to date with the published syllabus: when an edition now applies (published after
    /// the paper was read, or a nearer one), the suggestions already stored with the paper and mark scheme readings
    /// are linked again — no file is read again and no AI is called. Parts the teacher decided are left alone.
    /// </summary>
    public async Task RefreshTopicsAsync(CambridgeImport i)
    {
        var version = await VersionForAsync(i);
        if (version == i.SyllabusVersionId) return;
        i.SyllabusVersionId = version;
        var (topics, _) = await TopicsAsync(i);
        foreach (var p in i.Parts.Where(p => p.TopicSource != "teacher"))
        {
            p.SyllabusTopicId = null; p.SyllabusTopic = null; p.TopicSource = "none"; p.TopicNote = null;
        }
        if (topics.Count > 0) ApplyStoredSuggestions(i, topics);
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>The topics named in the stored readings (paper, then mark scheme), for parts not decided yet.</summary>
    private static void ApplyStoredSuggestions(CambridgeImport i, IReadOnlyList<CambridgeImportLogic.TopicRef> topics)
    {
        if (i.PaperJson is not null)
        {
            try
            {
                foreach (var q in CambridgeImportLogic.ParsePaper(i.PaperJson).Questions)
                    foreach (var pp in q.Parts.Count > 0 ? q.Parts : new List<CambridgeImportLogic.PaperPart> { new() { Label = "" } })
                        Pick(i, topics, q.Number + CambridgeImportLogic.Suffix(pp.Label ?? ""), pp.Topic, pp.TopicConfidence, "suggested");
            }
            catch (JsonException) { }
        }
        if (i.MarkSchemeJson is not null)
        {
            try { ApplyPicks(i, CambridgeImportLogic.ParseScheme(i.MarkSchemeJson).Topics, topics, "scheme"); }
            catch (JsonException) { }
        }
    }

    /// <summary>Links the model's topic picks to the parts still without a topic; returns how many were linked.</summary>
    private static int ApplyPicks(CambridgeImport i, IEnumerable<CambridgeImportLogic.TopicPick>? picks, IReadOnlyList<CambridgeImportLogic.TopicRef> topics, string source)
    {
        var n = 0;
        foreach (var pick in picks ?? Enumerable.Empty<CambridgeImportLogic.TopicPick>())
            if (Pick(i, topics, CambridgeImportLogic.Normalise(pick.Part), pick.Topic, pick.TopicConfidence, source)) n++;
        return n;
    }

    private static bool Pick(CambridgeImport i, IReadOnlyList<CambridgeImportLogic.TopicRef> topics, string number, string? topic, string? confidence, string source)
    {
        var p = i.Parts.FirstOrDefault(x => x.Number == number);
        if (p is null || p.SyllabusTopicId is not null || p.TopicSource == "teacher") return false;
        var (id, note) = CambridgeImportLogic.ResolveTopic(topic, confidence, topics);
        if (id is null)
        {
            if (topic is not null || p.TopicNote is null) p.TopicNote = note;   // keep an earlier, more useful note
            return false;
        }
        p.SyllabusTopicId = id; p.SyllabusTopic = null; p.TopicSource = source; p.TopicNote = null;
        p.UpdatedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>Marks a stage as running before it is handed to the background, so the page shows it at once.</summary>
    public async Task<bool> BeginAsync(CambridgeImport i, bool markScheme)
    {
        if ((markScheme ? i.MarkSchemeStage : i.PaperStage) == ImportStage.Running) return false;
        if (markScheme) { i.MarkSchemeStage = ImportStage.Running; i.MarkSchemeError = null; }
        else { i.PaperStage = ImportStage.Running; i.PaperError = null; }
        i.StageStartedAt = DateTime.UtcNow;
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Reads the Question Paper (background). Done once: a stored reading is reused.</summary>
    public async Task RunPaperAsync(Guid importId, CancellationToken ct = default)
    {
        var i = await _db.CambridgeImports.Include(x => x.Parts).FirstOrDefaultAsync(x => x.Id == importId, ct);
        if (i is null) return;
        try
        {
            if (i.PaperJson is null)
            {
                var pdf = await File.ReadAllBytesAsync(LibraryFileStorage.ImportPdfPath(_env, i.Id, "paper"), ct);
                var (_, topicText) = await TopicsAsync(i);
                var json = await _reader.ReadPaperAsync(pdf, topicText, i.Id, ct);
                CambridgeImportLogic.ParsePaper(json);            // refuse a reply that cannot be used before storing it
                i.PaperJson = json;
            }
            if (i.Parts.Count == 0)
            {
                var (topicRefs, _) = await TopicsAsync(i);
                // Added through the set: their ids are set in code, which EF would otherwise take for existing rows.
                foreach (var p in CambridgeImportLogic.BuildParts(CambridgeImportLogic.ParsePaper(i.PaperJson), topicRefs))
                {
                    p.ImportId = i.Id;
                    _db.CambridgeImportParts.Add(p);
                }
            }
            i.PaperStage = ImportStage.Done;
            i.PaperError = null;
            i.LastCompletedStep = Math.Max(i.LastCompletedStep, 1);
        }
        catch (Exception ex) when (ex is AiCallException or JsonException or IOException)
        {
            _logger.LogWarning("Cambridge import {Import}: paper reading failed ({Kind})", i.Id, (ex as AiCallException)?.Kind ?? ex.GetType().Name);
            i.PaperStage = ImportStage.Failed;
            i.PaperError = Friendly(ex);
        }
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The teacher's check of the detected details. A different year can mean a different syllabus version:
    /// topics are carried over by their number within the component.</summary>
    public async Task SaveDetailsAsync(CambridgeImport i, int? component, string? variant, string? session, int? year, string? subject)
    {
        var oldVersion = i.SyllabusVersionId;
        var oldComponent = i.Component;
        i.Component = component is >= 1 and <= 6 ? component : null;
        i.Variant = string.IsNullOrWhiteSpace(variant) || variant.Trim() is "-" or "—" ? null : variant.Trim()[..1];
        i.Session = string.IsNullOrWhiteSpace(session) ? null : session.Trim();
        i.Year = year is >= 2000 and <= 2100 ? year : null;
        i.Subject = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        i.DetailsChecked = true;
        i.SyllabusVersionId = await VersionForAsync(i);
        if (i.SyllabusVersionId != oldVersion || i.Component != oldComponent)
        {
            var (topics, _) = await TopicsAsync(i);
            var byNumber = topics.Where(t => t.Number != null).GroupBy(t => t.Number!).ToDictionary(g => g.Key, g => g.First().Id);
            foreach (var p in i.Parts.Where(p => p.Status != ImportPartStatus.Saved && p.SyllabusTopicId is not null))
            {
                var number = p.SyllabusTopic?.Number ?? await _db.SyllabusTopics.Where(t => t.Id == p.SyllabusTopicId).Select(t => t.Number).FirstOrDefaultAsync();
                p.SyllabusTopicId = number is not null && byNumber.TryGetValue(number, out var id) ? id : null;
                p.SyllabusTopic = null;
                if (p.SyllabusTopicId is null && p.TopicSource != "teacher") p.TopicSource = "none";
            }
            // Parts without a topic get the stored suggestions linked to the new edition.
            if (topics.Count > 0) ApplyStoredSuggestions(i, topics);
        }
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>The teacher's topic for a subpart: any topic of a published version, or none.</summary>
    public async Task<bool> SetTopicAsync(CambridgeImportPart p, Guid? topicId)
    {
        if (topicId is { } t && !await _db.SyllabusTopics.AnyAsync(x => x.Id == t && x.Component.Version.Status == SyllabusVersionStatus.Published))
            return false;
        p.SyllabusTopicId = topicId;
        p.SyllabusTopic = null;
        p.TopicSource = "teacher";             // also a deliberate "no topic": never filled in automatically
        p.TopicNote = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    // ── Step 2: Mark Scheme ──────────────────────────────────────────────────────────────────────────────────────
    public sealed record SchemeCheck(PaperDetails Scheme, List<(string Field, string Paper, string Scheme)> Conflicts, bool Unreadable, string? Error);

    public PaperDetails PaperDetailsOf(CambridgeImport i) =>
        new(i.SyllabusCode, i.Component, i.Variant, i.Session, i.Year, false, i.PaperPages);

    public static PaperDetails? SchemeDetailsOf(CambridgeImport i) =>
        i.MarkSchemeDetailsJson is null ? null : JsonSerializer.Deserialize<PaperDetails>(i.MarkSchemeDetailsJson);

    /// <summary>Checks a mark scheme against the paper by both documents' own text, then stores it. A clear conflict
    /// is returned for the teacher (the file is kept so it can be compared, but it is not read); details that could
    /// not be read need the teacher's confirmation first.</summary>
    public async Task<SchemeCheck> AttachMarkSchemeAsync(CambridgeImport i, byte[] pdf, string fileName)
    {
        var details = PaperDetails.Read(pdf);
        if (details.Pages == 0) return new SchemeCheck(details, new(), false, "This file could not be opened as a PDF.");
        if (!details.IsMarkScheme && details.Complete)
            return new SchemeCheck(details, new(), false, "This looks like a Question Paper, not a Mark Scheme.");

        var (conflicts, unreadable) = PaperDetails.Compare(PaperDetailsOf(i), details);
        var sha = Sha(pdf);
        if (sha != i.MarkSchemeSha256)
        {
            await LibraryFileStorage.SaveImportPdfAsync(_env, i.Id, "markscheme", pdf);
            i.MarkSchemeSha256 = sha;
            i.MarkSchemeFileName = Path.GetFileName(fileName);
            i.MarkSchemePages = details.Pages;
            i.MarkSchemeDetailsJson = JsonSerializer.Serialize(details);
            i.MarkSchemeDetailsConfirmed = false;
            i.MarkSchemeJson = null;                    // a different file is read afresh
            i.MarkSchemeStage = ImportStage.NotStarted;
            i.MarkSchemeError = null;
            i.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return new SchemeCheck(details, conflicts, unreadable, null);
    }

    /// <summary>Whether the stored mark scheme may be read now: no conflict, and readable details or the teacher's OK.</summary>
    public (bool Ready, List<(string Field, string Paper, string Scheme)> Conflicts, bool NeedsConfirmation) SchemeReadiness(CambridgeImport i)
    {
        var s = SchemeDetailsOf(i);
        if (s is null) return (false, new(), false);
        var (conflicts, unreadable) = PaperDetails.Compare(PaperDetailsOf(i), s);
        var needsOk = unreadable && !i.MarkSchemeDetailsConfirmed;
        return (conflicts.Count == 0 && !needsOk, conflicts, needsOk);
    }

    public async Task ConfirmSchemeDetailsAsync(CambridgeImport i)
    {
        i.MarkSchemeDetailsConfirmed = true;
        await _db.SaveChangesAsync();
    }

    /// <summary>Reads the Mark Scheme (background) and matches it to the subparts. A stored reading is reused, so a
    /// retry after a failure only does what failed.</summary>
    public async Task RunMarkSchemeAsync(Guid importId, CancellationToken ct = default)
    {
        var i = await _db.CambridgeImports.Include(x => x.Parts).FirstOrDefaultAsync(x => x.Id == importId, ct);
        if (i is null) return;
        try
        {
            if (i.MarkSchemeJson is null)
            {
                var pdf = await File.ReadAllBytesAsync(LibraryFileStorage.ImportPdfPath(_env, i.Id, "markscheme"), ct);
                var partList = string.Join("\n", i.Parts.OrderBy(p => p.Order).Select(p => $"{p.Number} [{p.Marks?.ToString() ?? "?"}]"));
                var (topicRefs, topicText) = await TopicsAsync(i);
                var open = i.Parts.Where(p => p.SyllabusTopicId is null && p.TopicSource != "teacher").OrderBy(p => p.Order).ToList();
                if (topicRefs.Count > 0 && open.Count > 0)
                    partList += "\n\nSUBPARTS WITHOUT A CONFIDENT TOPIC (number: text):\n"
                              + string.Join("\n", open.Select(p => $"{p.Number}: {Short(p.Stem, 300)} {Short(p.Text, 400)}".Trim()))
                              + "\n\nSYLLABUS TOPICS OF THIS COMPONENT (code title):\n" + topicText;
                var json = await _reader.ReadMarkSchemeAsync(pdf, partList, i.Id, ct);
                CambridgeImportLogic.ParseScheme(json);
                i.MarkSchemeJson = json;
            }
            var reading = CambridgeImportLogic.ParseScheme(i.MarkSchemeJson);
            ApplyScheme(i, reading);
            var (refs, _) = await TopicsAsync(i);
            if (refs.Count > 0) ApplyPicks(i, reading.Topics, refs, "scheme");
            i.MarkSchemeStage = ImportStage.Done;
            i.MarkSchemeError = null;
            i.LastCompletedStep = Math.Max(i.LastCompletedStep, 2);
        }
        catch (Exception ex) when (ex is AiCallException or JsonException or IOException)
        {
            _logger.LogWarning("Cambridge import {Import}: mark scheme reading failed ({Kind})", i.Id, (ex as AiCallException)?.Kind ?? ex.GetType().Name);
            i.MarkSchemeStage = ImportStage.Failed;
            i.MarkSchemeError = Friendly(ex);
        }
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    // ── Suggest missing topics ───────────────────────────────────────────────────────────────────────────────────
    /// <summary>The parts a suggestion would fill: no topic yet and not decided by the teacher.</summary>
    public static List<CambridgeImportPart> OpenTopicParts(CambridgeImport i) =>
        i.Parts.Where(p => p.SyllabusTopicId is null && p.TopicSource != "teacher").OrderBy(p => p.Order).ToList();

    public async Task<bool> BeginTopicsAsync(CambridgeImport i)
    {
        if (i.TopicStage == ImportStage.Running || i.PaperStage != ImportStage.Done || OpenTopicParts(i).Count == 0) return false;
        if ((await TopicsAsync(i)).Topics.Count == 0) return false;
        i.TopicStage = ImportStage.Running; i.TopicError = null; i.StageStartedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Suggests topics for the parts still without one from what is already stored — each part's text, the shared
    /// stem, the figure's description, the part before it (for "Hence …") and its mark scheme criteria — in one
    /// text-only call; no PDF is read again. Only parts that are still open when the answer arrives are filled, so a
    /// choice the teacher made meanwhile stays.
    /// </summary>
    public async Task RunTopicsAsync(Guid importId, CancellationToken ct = default)
    {
        var i = await _db.CambridgeImports.Include(x => x.Parts).FirstOrDefaultAsync(x => x.Id == importId, ct);
        if (i is null) return;
        try
        {
            var (topics, topicText) = await TopicsAsync(i);
            var ordered = i.Parts.OrderBy(p => p.Order).ToList();
            var open = OpenTopicParts(i);
            var payload = open.Select(p =>
            {
                var before = ordered.LastOrDefault(x => x.QuestionNumber == p.QuestionNumber && x.Order < p.Order);
                return new
                {
                    part = p.Number, stem = p.Stem, figure = p.FigureDescription, text = p.Text,
                    previousPart = before is null ? null : new { number = before.Number, text = Short(before.Text, 400) },
                    markScheme = Short(p.Criteria, 900),
                };
            }).ToList();
            // Mathematics as written ("+", "<"), not as escapes: the model reads this text.
            var partsJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var json = await _reader.SuggestTopicsAsync(partsJson, topicText, i.Id, ct);
            var picks = CambridgeImportLogic.ParseTopics(json).Topics;

            // The teacher may have changed topics while the model was thinking: apply to the parts as they are now.
            _db.ChangeTracker.Clear();
            i = await _db.CambridgeImports.Include(x => x.Parts).FirstAsync(x => x.Id == importId, CancellationToken.None);
            var linked = ApplyPicks(i, picks, topics, "classifier");
            await FillSavedQuestionsAsync(i);
            i.TopicStage = ImportStage.Done;
            i.TopicError = null;
            _logger.LogInformation("Cambridge import {Import}: {Linked} of {Open} open parts got a topic", i.Id, linked, open.Count);
        }
        catch (Exception ex) when (ex is AiCallException or JsonException)
        {
            _logger.LogWarning("Cambridge import {Import}: topic suggestion failed ({Kind})", i.Id, (ex as AiCallException)?.Kind ?? ex.GetType().Name);
            _db.ChangeTracker.Clear();
            i = await _db.CambridgeImports.FirstAsync(x => x.Id == importId, CancellationToken.None);
            i.TopicStage = ImportStage.Failed;
            i.TopicError = Friendly(ex);
        }
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>A part saved to the library before it had a topic: its library question gets the topic too, but only
    /// while that question is untouched since it was saved (no topic, never edited) — a teacher's edit, including
    /// removing a topic there, is kept.</summary>
    private async Task FillSavedQuestionsAsync(CambridgeImport i)
    {
        foreach (var p in i.Parts.Where(p => p.Status == ImportPartStatus.Saved && p.SavedQuestionId is not null && p.SyllabusTopicId is not null && p.TopicSource != "teacher"))
        {
            var q = await _db.Questions.FirstOrDefaultAsync(x => x.Id == p.SavedQuestionId);
            if (q is not null && q.SyllabusTopicId is null && q.UpdatedAt is null) q.SyllabusTopicId = p.SyllabusTopicId;
        }
    }

    private static string? Short(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Length <= max ? s.Trim() : s[..max].Trim() + " …";

    /// <summary>Mark scheme rows that matched no subpart in the last reading (shown on step 2).</summary>
    public static List<string> UnusedRows(CambridgeImport i)
    {
        if (i.MarkSchemeJson is null) return new();
        try
        {
            return CambridgeImportLogic.Match(i.Parts.Select(p => (p.Number, p.Marks)).ToList(),
                CambridgeImportLogic.ParseScheme(i.MarkSchemeJson)).UnusedRows;
        }
        catch (JsonException) { return new(); }
    }

    public sealed record Replacement(string? AnswerKey, string? Criteria, int? SchemeMarks, string? SchemePages, ImportPartStatus Status, string? Reason);

    /// <summary>Applies a reading. A part the teacher already confirmed, edited or saved is never overwritten: when the
    /// new reading differs it is kept aside as a pending replacement for the teacher to accept or reject.</summary>
    public static void ApplyScheme(CambridgeImport i, CambridgeImportLogic.SchemeReading scheme)
    {
        var (matches, _) = CambridgeImportLogic.Match(i.Parts.Select(p => (p.Number, p.Marks)).ToList(), scheme);
        foreach (var m in matches)
        {
            var p = i.Parts.First(x => x.Number == m.Number);
            var protectedPart = p.EditedByTeacher || p.Status is ImportPartStatus.Confirmed or ImportPartStatus.Saved;
            if (protectedPart)
            {
                var differs = Norm(p.AnswerKey) != Norm(m.AnswerKey) || Norm(p.Criteria) != Norm(m.Criteria);
                p.PendingReplacement = differs
                    ? JsonSerializer.Serialize(new Replacement(m.AnswerKey, m.Criteria, m.SchemeMarks, m.Pages, m.Status, m.Reason))
                    : null;
                continue;
            }
            p.AnswerKey = m.AnswerKey;
            p.Criteria = m.Criteria;
            p.SchemeMarks = m.SchemeMarks;
            p.SchemePages = m.Pages;
            // An answer format still to be chosen keeps the part in review, whatever the mark scheme says.
            p.Status = p.FormatQuestion is not null ? ImportPartStatus.NeedsReview : m.Status;
            p.ReviewReason = m.Reason;
            p.PendingReplacement = null;
        }
    }

    private static string Norm(string? s) => string.Join(" ", (s ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static Replacement? PendingOf(CambridgeImportPart p) =>
        p.PendingReplacement is null ? null : JsonSerializer.Deserialize<Replacement>(p.PendingReplacement);

    // ── Step 3: review ───────────────────────────────────────────────────────────────────────────────────────────
    public async Task<string?> ConfirmAsync(CambridgeImportPart p)
    {
        if (p.Status == ImportPartStatus.Saved) return "This part is already saved.";
        if (p.PendingReplacement is not null) return "Choose between your version and the new reading first.";
        if (p.FormatQuestion is not null) return "Choose a drawn or a written answer first.";
        if (string.IsNullOrWhiteSpace(p.AnswerKey) && string.IsNullOrWhiteSpace(p.Criteria))
            return "Add an answer key or marking criteria before confirming.";
        p.Status = ImportPartStatus.Confirmed;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>Confirms the chosen parts that matched cleanly (Matched, nothing pending); returns how many.</summary>
    public async Task<int> ConfirmMatchedAsync(CambridgeImport i, IReadOnlyCollection<Guid> partIds)
    {
        var n = 0;
        foreach (var p in i.Parts.Where(p => partIds.Contains(p.Id) && p.Status == ImportPartStatus.Matched && p.PendingReplacement is null && p.FormatQuestion is null))
        { p.Status = ImportPartStatus.Confirmed; p.UpdatedAt = DateTime.UtcNow; n++; }
        await _db.SaveChangesAsync();
        return n;
    }

    public async Task<string?> EditAsync(CambridgeImportPart p, string? answerKey, string? criteria, int? marks)
    {
        if (p.Status == ImportPartStatus.Saved) return "This part is already in your Content Library; edit it there.";
        p.AnswerKey = string.IsNullOrWhiteSpace(answerKey) ? null : answerKey.Trim();
        p.Criteria = string.IsNullOrWhiteSpace(criteria) ? null : criteria.Trim();
        if (marks is > 0 and <= 100) p.Marks = marks;
        p.EditedByTeacher = true;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return null;
    }

    /// <summary>The teacher's choice of answer format: drawn on the sketch pad, or written. A part that was in review
    /// only for its format becomes Matched again.</summary>
    public async Task<string?> SetFormatAsync(CambridgeImportPart p, bool drawing)
    {
        if (p.Status == ImportPartStatus.Saved) return "This part is already in your Content Library; edit it there.";
        p.NeedsDrawing = drawing;
        if (drawing) p.Kind = "OpenText";
        var wasOpen = p.FormatQuestion is not null;
        p.FormatQuestion = null;
        if (wasOpen && p.Status == ImportPartStatus.NeedsReview && p.ReviewReason is null && p.PendingReplacement is null)
            p.Status = p.AnswerKey is null && p.Criteria is null ? ImportPartStatus.Recognised : ImportPartStatus.Matched;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task ResolvePendingAsync(CambridgeImportPart p, bool useNew)
    {
        var r = PendingOf(p);
        if (r is null) return;
        if (useNew && p.Status != ImportPartStatus.Saved)
        {
            p.AnswerKey = r.AnswerKey; p.Criteria = r.Criteria; p.SchemeMarks = r.SchemeMarks; p.SchemePages = r.SchemePages;
            p.Status = p.FormatQuestion is not null ? ImportPartStatus.NeedsReview : r.Status;
            p.ReviewReason = r.Reason; p.EditedByTeacher = false;
        }
        p.PendingReplacement = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ── Save to the Content Library ──────────────────────────────────────────────────────────────────────────────
    public sealed record SaveResult(int Saved, int AlreadySaved, List<Guid> QuestionIds);

    /// <summary>The identity of a source question in its owner's library: syllabus, paper file and question number.
    /// A subpart's key adds its full number.</summary>
    public static string SourceGroup(CambridgeImport i, int? question = null) =>
        $"{i.SyllabusCode}:{i.PaperSha256[..16]}#{question}";

    /// <summary>
    /// Saves the chosen confirmed parts as the owner's Content Library questions. A part saved before (by this owner,
    /// from the same paper) is linked to the existing question instead of being saved twice — also when the same
    /// click arrives twice.
    /// </summary>
    public async Task<SaveResult> SaveConfirmedAsync(CambridgeImport i, IReadOnlyCollection<Guid>? partIds = null)
    {
        var chosen = i.Parts.Where(p => p.Status == ImportPartStatus.Confirmed && (partIds is null || partIds.Contains(p.Id))).ToList();
        int saved = 0, already = 0;
        var ids = new List<Guid>();
        foreach (var p in chosen)
        {
            var key = $"{SourceGroup(i, p.QuestionNumber)}:{p.Number}";
            var existing = await _db.Questions.FirstOrDefaultAsync(q => q.CreatedByEmail == i.OwnerEmail && q.SourceKey == key);
            if (existing is not null)
            {
                p.SavedQuestionId = existing.Id; p.Status = ImportPartStatus.Saved; already++; ids.Add(existing.Id);
                continue;
            }
            var q = new Question
            {
                Title = p.Text,
                Type = p.Kind == "OpenText" || p.NeedsDrawing ? QuestionType.OpenText : QuestionType.ShortAnswer,
                RequireDrawing = p.NeedsDrawing,
                Difficulty = DifficultyLevel.Medium,
                Subject = i.Subject,
                AnswerKey = p.AnswerKey ?? "",
                MarkScheme = p.Criteria,
                Marks = p.Marks ?? p.SchemeMarks,
                Stem = p.Stem,
                SyllabusTopicId = p.SyllabusTopicId,
                SourceLabel = $"{i.Label} · Q{p.Number}",
                SourceKey = key,
                SourceGroupKey = SourceGroup(i, p.QuestionNumber),
                CreatedByEmail = i.OwnerEmail,
                IsPublic = false,
            };
            q.ImagePath = LibraryFileStorage.CopyFigureToLibrary(_env, i.Id, p.FigureKey, q.Id);
            _db.Questions.Add(q);
            try
            {
                p.SavedQuestionId = q.Id; p.Status = ImportPartStatus.Saved;
                await _db.SaveChangesAsync();
                saved++; ids.Add(q.Id);
            }
            catch (DbUpdateException)
            {
                // The same part saved by a second click at the same moment: use the copy that won.
                _db.Entry(q).State = EntityState.Detached;
                var winner = await _db.Questions.AsNoTracking().FirstOrDefaultAsync(x => x.CreatedByEmail == i.OwnerEmail && x.SourceKey == key);
                if (winner is null) throw;
                p.SavedQuestionId = winner.Id; p.Status = ImportPartStatus.Saved; already++; ids.Add(winner.Id);
            }
        }
        if (chosen.Count > 0) i.LastCompletedStep = Math.Max(i.LastCompletedStep, 3);
        i.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return new SaveResult(saved, already, ids);
    }

    public async Task DiscardAsync(CambridgeImport i)
    {
        i.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        LibraryFileStorage.DeleteImport(_env, i.Id);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────
    public static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Friendly(Exception ex) => ex switch
    {
        AiCallException { Kind: "not-configured" } => "AI reading is not configured on this server. Your draft is saved.",
        AiCallException { Kind: "timeout" } => "The AI service did not answer in time. Your draft is saved — try again.",
        AiCallException => "The AI service is temporarily unavailable. Your draft is saved — try again.",
        JsonException => "The AI reply could not be used. Your draft is saved — try again.",
        _ => "The file could not be read. Upload it again.",
    };
}
