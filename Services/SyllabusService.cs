using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Data;
using SmartSchoolAI.Models;

namespace SmartSchoolAI.Services;

/// <summary>
/// The syllabus library: importing a syllabus PDF as a draft, correcting it, publishing it, and the published topics
/// teachers link questions to. Everything is local — no AI call is involved in reading, searching or choosing topics.
/// </summary>
public class SyllabusService
{
    private readonly AppDbContext _db;
    public SyllabusService(AppDbContext db) => _db = db;

    /// <summary>What an import did.</summary>
    public enum ImportOutcome
    {
        /// <summary>A new version was created (as a draft).</summary>
        Imported,
        /// <summary>This exact file is already in the library; that version is returned unchanged.</summary>
        SameFile,
        /// <summary>A different file for a programme and exam years already in the library. Nothing is replaced: the
        /// stored edition, its published topics and every question link stay as they are.</summary>
        DifferentFileSameYears,
        Failed,
    }

    /// <summary>The file that was uploaded, described so the administrator can compare it with the stored edition.</summary>
    public sealed record UploadedFile(string Name, string Sha256, string? DocumentVersion, int Components, int Topics);

    public sealed record ImportResult(ImportOutcome Outcome, SyllabusVersion? Version, string? Error, UploadedFile? Uploaded = null)
    {
        public bool AlreadyInLibrary => Outcome is ImportOutcome.SameFile or ImportOutcome.DifferentFileSameYears;
    }

    /// <summary>
    /// Imports a syllabus PDF as a draft. The same file again returns the version already in the library. A
    /// different file for the same programme and exam years does not create a second copy or overwrite anything:
    /// it returns the stored version with <see cref="ImportOutcome.DifferentFileSameYears"/> and a description of
    /// the uploaded file, so the administrator can see that the files differ and which edition is kept.
    /// </summary>
    public async Task<ImportResult> ImportAsync(byte[] pdf, string fileName, string? sourceUrl, Guid? userId)
    {
        var sha = Convert.ToHexString(SHA256.HashData(pdf)).ToLowerInvariant();
        var same = await _db.SyllabusVersions.FirstOrDefaultAsync(v => v.SourceSha256 == sha);
        if (same is not null) return new ImportResult(ImportOutcome.SameFile, same, null);

        var parsed = SyllabusPdfParser.Parse(pdf);
        if (parsed.Code is null || parsed.FirstYear is null || parsed.Components.Count == 0)
            return new ImportResult(ImportOutcome.Failed, null, "This file does not look like a Cambridge syllabus: " + string.Join(" ", parsed.Problems));

        var program = await _db.SyllabusPrograms.FirstOrDefaultAsync(p => p.Code == parsed.Code);
        if (program is null)
        {
            program = new SyllabusProgram { Code = parsed.Code, Title = parsed.Title ?? parsed.Code };
            _db.SyllabusPrograms.Add(program);
        }
        else
        {
            var sameYears = await _db.SyllabusVersions.FirstOrDefaultAsync(v =>
                v.ProgramId == program.Id && v.FirstExamYear == parsed.FirstYear && v.LastExamYear == (parsed.LastYear ?? parsed.FirstYear));
            if (sameYears is not null)
                return new ImportResult(ImportOutcome.DifferentFileSameYears, sameYears, null, Describe(parsed, fileName, sha));
        }

        var version = new SyllabusVersion
        {
            Program = program,
            FirstExamYear = parsed.FirstYear.Value,
            LastExamYear = parsed.LastYear ?? parsed.FirstYear.Value,
            DocumentVersion = parsed.DocumentVersion,
            SourceName = Path.GetFileName(fileName),
            SourceUrl = string.IsNullOrWhiteSpace(sourceUrl) ? null : sourceUrl.Trim(),
            SourceSha256 = sha,
            ImportedById = userId,
        };
        foreach (var c in parsed.Components)
        {
            var comp = new SyllabusComponent { Number = c.Number, Title = c.Title, SourcePage = c.Page };
            var order = 0;
            foreach (var t in c.Topics)
                comp.Topics.Add(new SyllabusTopic { Number = t.Number, Title = t.Title, SourcePage = t.Page, Order = ++order });
            version.Components.Add(comp);
        }
        _db.SyllabusVersions.Add(version);
        try { await _db.SaveChangesAsync(); }
        catch (DbUpdateException)
        {
            // Two imports at once: the unique keys let one win; return that one.
            _db.ChangeTracker.Clear();
            var winner = await _db.SyllabusVersions.FirstOrDefaultAsync(v => v.SourceSha256 == sha);
            if (winner is not null) return new ImportResult(ImportOutcome.SameFile, winner, null);
            var sameYears = await _db.SyllabusVersions.FirstOrDefaultAsync(v => v.Program.Code == parsed.Code
                && v.FirstExamYear == parsed.FirstYear && v.LastExamYear == (parsed.LastYear ?? parsed.FirstYear));
            if (sameYears is null) throw;
            return new ImportResult(ImportOutcome.DifferentFileSameYears, sameYears, null, Describe(parsed, fileName, sha));
        }
        return new ImportResult(ImportOutcome.Imported, version, null);
    }

    private static UploadedFile Describe(SyllabusPdfParser.ParsedSyllabus parsed, string fileName, string sha) =>
        new(Path.GetFileName(fileName), sha, parsed.DocumentVersion, parsed.Components.Count, parsed.Components.Sum(c => c.Topics.Count));

    public Task<List<SyllabusVersion>> VersionsAsync() =>
        _db.SyllabusVersions.Include(v => v.Program).Include(v => v.Components).ThenInclude(c => c.Topics)
            .OrderBy(v => v.Program.Code).ThenByDescending(v => v.FirstExamYear).AsSplitQuery().ToListAsync();

    public async Task<SyllabusVersion?> VersionAsync(Guid id)
    {
        var v = await _db.SyllabusVersions.Include(x => x.Program)
            .Include(x => x.Components).ThenInclude(c => c.Topics)
            .AsSplitQuery().FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return null;
        v.Components = v.Components.OrderBy(c => c.Number).ToList();
        foreach (var c in v.Components) c.Topics = c.Topics.OrderBy(t => t.Order).ToList();
        return v;
    }

    /// <summary>How many questions link to each topic of a version (for the review page and for removal checks).</summary>
    public async Task<Dictionary<Guid, int>> LinkCountsAsync(Guid versionId) =>
        await _db.Questions.IgnoreQueryFilters()
            .Where(q => q.SyllabusTopicId != null && q.SyllabusTopic!.Component.VersionId == versionId)
            .GroupBy(q => q.SyllabusTopicId!.Value)
            .Select(g => new { g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.N);

    public sealed record TopicEdit(Guid Id, string? Number, string Title, int? Page, bool Remove);
    public sealed record TopicAddition(Guid ComponentId, string? Number, string Title, int? Page);

    /// <summary>
    /// Saves an administrator's corrections. Topics keep their ids, so a corrected title reaches every linked
    /// question. A topic that questions are linked to cannot be removed.
    /// </summary>
    public async Task<string?> SaveEditsAsync(Guid versionId, IEnumerable<TopicEdit> edits, IEnumerable<TopicAddition> additions)
    {
        var v = await VersionAsync(versionId);
        if (v is null) return "This syllabus version no longer exists.";
        var topics = v.Components.SelectMany(c => c.Topics).ToDictionary(t => t.Id);
        var links = await LinkCountsAsync(versionId);

        foreach (var e in edits)
        {
            if (!topics.TryGetValue(e.Id, out var t)) continue;
            if (e.Remove)
            {
                if (links.GetValueOrDefault(t.Id) > 0)
                    return $"\"{t.Title}\" has questions linked to it, so it stays in the library. Correct its title instead.";
                _db.SyllabusTopics.Remove(t);
                continue;
            }
            var title = (e.Title ?? "").Trim();
            if (title.Length == 0) return $"Topic {t.Number} needs a title.";
            t.Title = title.Length > 200 ? title[..200] : title;
            t.Number = string.IsNullOrWhiteSpace(e.Number) ? null : e.Number.Trim();
            t.SourcePage = e.Page;
        }
        foreach (var a in additions)
        {
            if (string.IsNullOrWhiteSpace(a.Title)) continue;
            var comp = v.Components.FirstOrDefault(c => c.Id == a.ComponentId);
            if (comp is null) continue;
            comp.Topics.Add(new SyllabusTopic
            {
                Title = a.Title.Trim(), Number = string.IsNullOrWhiteSpace(a.Number) ? null : a.Number.Trim(),
                SourcePage = a.Page, Order = comp.Topics.Count == 0 ? 1 : comp.Topics.Max(x => x.Order) + 1,
            });
        }
        await _db.SaveChangesAsync();
        return null;
    }

    public async Task<bool> PublishAsync(Guid versionId)
    {
        var v = await _db.SyllabusVersions.FirstOrDefaultAsync(x => x.Id == versionId);
        if (v is null) return false;
        if (v.Status != SyllabusVersionStatus.Published)
        {
            v.Status = SyllabusVersionStatus.Published;
            v.PublishedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return true;
    }

    /// <summary>Deletes a draft (never a published version: questions may be linked to it).</summary>
    public async Task<bool> DiscardDraftAsync(Guid versionId)
    {
        var v = await _db.SyllabusVersions.FirstOrDefaultAsync(x => x.Id == versionId && x.Status == SyllabusVersionStatus.Draft);
        if (v is null) return false;
        _db.SyllabusVersions.Remove(v);
        await _db.SaveChangesAsync();
        return true;
    }

    // ── What teachers see ────────────────────────────────────────────────────

    public sealed record CatalogTopic(Guid Id, string? Number, string Title);
    public sealed record CatalogComponent(Guid Id, int Number, string Title, List<CatalogTopic> Topics);
    public sealed record CatalogVersion(Guid Id, string Code, string Program, string Years, string Label, List<CatalogComponent> Components);

    /// <summary>Published versions with their components and topics, newest exam years first.</summary>
    public async Task<List<CatalogVersion>> CatalogAsync()
    {
        var versions = await _db.SyllabusVersions.AsNoTracking()
            .Where(v => v.Status == SyllabusVersionStatus.Published)
            .Include(v => v.Program).Include(v => v.Components).ThenInclude(c => c.Topics)
            .AsSplitQuery().ToListAsync();
        return versions
            .OrderBy(v => v.Program.Code).ThenByDescending(v => v.FirstExamYear)
            .Select(v => new CatalogVersion(v.Id, v.Program.Code, v.Program.Title, v.Years, $"{v.Program.Code} · {v.Years}",
                v.Components.OrderBy(c => c.Number).Select(c => new CatalogComponent(c.Id, c.Number, c.Title,
                    c.Topics.OrderBy(t => t.Order).Select(t => new CatalogTopic(t.Id, t.Number, t.Title)).ToList())).ToList()))
            .ToList();
    }

    /// <summary>A topic a teacher may link a question to: it exists in a published version.</summary>
    public Task<bool> IsLinkableAsync(Guid topicId) =>
        _db.SyllabusTopics.AnyAsync(t => t.Id == topicId && t.Component.Version.Status == SyllabusVersionStatus.Published);

    public sealed record TopicLabel(Guid TopicId, string Code, string Years, int ComponentNumber, string Component, string Topic)
    {
        /// <summary>"9709 · 2026–2027 · Pure Mathematics 1 · Quadratics"</summary>
        public string Full => $"{Code} · {Years} · {Component} · {Topic}";
        /// <summary>"9709 · P1 · Quadratics" — for a list row.</summary>
        public string Short => $"{Code} · P{ComponentNumber} · {Topic}";
    }

    public async Task<Dictionary<Guid, TopicLabel>> LabelsAsync(IEnumerable<Guid> topicIds)
    {
        var ids = topicIds.Distinct().ToList();
        if (ids.Count == 0) return new();
        var rows = await _db.SyllabusTopics.AsNoTracking().Where(t => ids.Contains(t.Id))
            .Select(t => new
            {
                t.Id, t.Title, Comp = t.Component.Title, CompNo = t.Component.Number,
                t.Component.Version.Program.Code, t.Component.Version.FirstExamYear, t.Component.Version.LastExamYear,
            }).ToListAsync();
        return rows.ToDictionary(r => r.Id, r => new TopicLabel(r.Id, r.Code,
            r.FirstExamYear == r.LastExamYear ? $"{r.FirstExamYear}" : $"{r.FirstExamYear}–{r.LastExamYear}",
            r.CompNo, r.Comp, r.Title));
    }
}
