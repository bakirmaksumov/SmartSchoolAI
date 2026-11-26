using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// "Import Cambridge Paper": details read from the documents, subpart numbering, matching mark scheme rows to subparts,
/// stored readings, review decisions and saving to the owner's library without duplicates. The documents and AI
/// replies here are generated TEST DATA shaped like a Cambridge 9709 paper — not Cambridge's material.
/// </summary>
public class CambridgeImportTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    private readonly FakeWebHostEnvironment _env = new();
    public void Dispose() { _factory.Dispose(); _env.Dispose(); }

    // ── Test documents ───────────────────────────────────────────────────────────────────────────────────────────
    private static byte[] PaperPdf(string code = "9709/12", string session = "May/June 2026", string marker = "") => TestPdf.Build(
        $"TEST DATA - not a Cambridge paper {marker}\nCambridge International AS & A Level\nMATHEMATICS {code}\nPaper 1 Pure Mathematics 1 {session}\n1 hour 50 minutes",
        $"10 The circle ... has centre C.\n(a) State the coordinates of C. [1]\n{code}/M/J/26");

    private static byte[] SchemePdf(string code = "9709/12", string session = "May/June 2026") => TestPdf.Build(
        $"TEST DATA - not a Cambridge mark scheme\nCambridge International AS & A Level\nMATHEMATICS {code}\nPaper 1 Pure Mathematics 1 {session}\nMARK SCHEME\nMaximum Mark: 75",
        "Question Answer Marks Partial Guidance\n10(a) (-2, 1) 1 B1");

    private const string PaperJson = """
    {"questions":[
      {"number":2,"stem":null,"figure":null,"parts":[{"label":"","text":"Solve the equation $f^{-1}(x) = gf(x)$.","marks":4,"page":2,"kind":"ShortAnswer","topic":"1.2","topicConfidence":"high"}]},
      {"number":10,"stem":"The circle $x^{2} + y^{2} + 4x - 2y - 20 = 0$ has centre $C$.","figure":null,"parts":[
        {"label":"(a)","text":"State the coordinates of $C$.","marks":1,"page":3,"topic":"1.3","topicConfidence":"high"},
        {"label":"(b)(i)","text":"It is given that the midpoint of $AB$ is $D(1.5, 1.5)$. Find the equation of $AB$.","marks":4,"page":3,"topic":"1.3","topicConfidence":"high"},
        {"label":"(b) (ii)","text":"It is given that the midpoint of $AB$ is $D(1.5, 1.5)$. Find the $x$-coordinates of $A$ and $B$.","marks":3,"page":3,"topic":"1.3","topicConfidence":"high"}]},
      {"number":12,"stem":"The diagram shows the curve $y = x(x-2)^{2}$.","figure":{"page":4,"box":[0.1,0.05,0.9,0.4],"description":"The curve with a shaded region"},"parts":[
        {"label":"(a)","text":"State the value of $a$.","marks":1,"page":4,"usesFigure":true,"topic":"1.7","topicConfidence":"high"},
        {"label":"(b)","text":"Calculate the value of $b$.","marks":4,"page":4,"usesFigure":true,"topic":"1.7","topicConfidence":"high"},
        {"label":"(c)","text":"Find the area of the shaded region.","marks":4,"page":4,"usesFigure":true,"topic":"1.8","topicConfidence":"low"}]}
    ]}
    """;

    private const string SchemeJson = """
    {"rows":[
      {"question":"12(a)","appliesTo":["12(a)"],"answer":"$a = 2$","codes":"B1","marks":1,"final":true,"page":11},
      {"question":"10(b)(i)","appliesTo":["10(b)(i)"],"answer":"Gradient $CD = \\frac{1}{7}$","codes":"M1","marks":1,"page":8},
      {"question":"10(b)(i)","appliesTo":["10(b)(i)"],"answer":"Gradient $AB = -7$","codes":"A1","marks":1,"page":8},
      {"question":"10(b)(i)","appliesTo":["10(b)(i)"],"answer":"$y - 1.5 = -7(x - 1.5)$","codes":"M1","marks":1,"guidance":"With gradient $-1/$their $m$","page":9},
      {"question":"10(b)(i)","appliesTo":["10(b)(i)"],"answer":"$y = -7x + 12$","codes":"A1","marks":1,"final":true,"page":9},
      {"question":"10(a)","appliesTo":["10(a)"],"answer":"$(-2, 1)$","codes":"B1","marks":1,"final":true,"page":8},
      {"question":"10(b)(ii)","appliesTo":["10(b)(ii)"],"answer":"$x^{2} + (12 - 7x)^{2} + 4x - 2(12 - 7x) - 20 = 0$","codes":"M1","marks":1,"page":9},
      {"question":"10(b)(ii)","appliesTo":["10(b)(ii)"],"answer":"$50x^{2} - 150x + 100 = 0$","codes":"A1","marks":1,"page":9},
      {"question":"10(b)(ii)","appliesTo":["10(b)(ii)"],"answer":"$x = 1, x = 2$","codes":"A1","marks":1,"final":true,"page":9},
      {"question":"10(b)(ii)","appliesTo":["10(b)(ii)"],"answer":"Use $D$ as midpoint: $x = 1.5 \\pm 0.5$","codes":"M1 A1","marks":2,"alternative":"Alternative method 1","page":10},
      {"question":"12(b)","appliesTo":["12(b)"],"answer":"$\\frac{dy}{dx} = 3x^{2} - 8x + 4 = 0$","codes":"M1 A1","marks":2,"page":11},
      {"question":"12(b)","appliesTo":["12(b)"],"answer":"$b = \\frac{2}{3}$","codes":"A1","marks":1,"final":true,"page":11},
      {"question":"2","appliesTo":["2"],"answer":"$\\frac{x-2}{3} = 12x - 4$","codes":"M1","marks":1,"page":5},
      {"question":"2","appliesTo":["2"],"answer":"$x = \\frac{2}{7}$","codes":"A1","marks":3,"final":true,"unreadable":true,"page":5},
      {"question":"13(a)","appliesTo":["13(a)"],"answer":"not on this paper","codes":"B1","marks":1,"page":12}
    ]}
    """;

    private sealed class FakeReader : ICambridgeAiReader
    {
        public int PaperCalls, SchemeCalls;
        public string Paper = PaperJson, Scheme = SchemeJson;
        public Exception? Fail;
        public Task<string> ReadPaperAsync(byte[] pdf, string topicList, Guid importId, CancellationToken ct = default)
        { PaperCalls++; if (Fail is not null) throw Fail; return Task.FromResult(Paper); }
        public Task<string> ReadMarkSchemeAsync(byte[] pdf, string partList, Guid importId, CancellationToken ct = default)
        { SchemeCalls++; if (Fail is not null) throw Fail; return Task.FromResult(Scheme); }
    }

    private readonly FakeReader _reader = new();
    private CambridgeImportService Svc() => new(_factory.Context, _env, _reader, NullLogger<CambridgeImportService>.Instance);

    private async Task<Dictionary<string, Guid>> SeedSyllabusAsync()
    {
        var program = new SyllabusProgram { Code = "9709", Title = "Mathematics" };
        var v = new SyllabusVersion { Program = program, FirstExamYear = 2026, LastExamYear = 2027, SourceName = "test", SourceSha256 = "x",
                                      Status = SyllabusVersionStatus.Published };
        var c = new SyllabusComponent { Number = 1, Title = "Pure Mathematics 1", SourcePage = 19 };
        var order = 0;
        foreach (var (n, t) in new[] { ("1.1", "Quadratics"), ("1.2", "Functions"), ("1.3", "Coordinate geometry"), ("1.7", "Differentiation"), ("1.8", "Integration") })
            c.Topics.Add(new SyllabusTopic { Number = n, Title = t, Order = ++order, SourcePage = 19 });
        v.Components.Add(c);
        _factory.Context.SyllabusVersions.Add(v);
        await _factory.Context.SaveChangesAsync();
        return c.Topics.ToDictionary(t => t.Number!, t => t.Id);
    }

    private static readonly Guid Owner = Guid.NewGuid();
    private const string OwnerEmail = "owner@school.test";

    private async Task<CambridgeImport> ReadPaperAsync(CambridgeImportService svc, byte[]? pdf = null, Guid? owner = null, string email = OwnerEmail)
    {
        var start = await svc.StartAsync(owner ?? Owner, email, pdf ?? PaperPdf(), "9709_s26_qp_12.pdf");
        Assert.Null(start.Error);
        await svc.RunPaperAsync(start.Import!.Id);
        return (await svc.LoadAsync(start.Import.Id, owner ?? Owner))!;
    }

    private async Task<CambridgeImport> ReadSchemeAsync(CambridgeImportService svc, CambridgeImport i)
    {
        var check = await svc.AttachMarkSchemeAsync(i, SchemePdf(), "9709_s26_ms_12.pdf");
        Assert.Empty(check.Conflicts);
        await svc.RunMarkSchemeAsync(i.Id);
        return (await svc.LoadAsync(i.Id, Owner))!;
    }

    // ── Details from the documents themselves ────────────────────────────────────────────────────────────────────
    [Fact]
    public void Details_are_read_from_the_cover_and_footers()
    {
        var real = PaperDetails.FromText(new[] { "Cambridge International AS & A Level\nMATHEMATICS9709/01\nPaper 1 Pure Mathematics 1   For examination from 2027\nSPECIMEN PAPER" });
        Assert.Equal(("9709", 1, (string?)null, "Specimen", 2027, false), (real.Code, real.Component, real.Variant, real.Session, real.Year, real.IsMarkScheme));

        var exam = PaperDetails.FromText(new[] { "MATHEMATICS 9709/12\nPaper 1 Pure Mathematics 1\nMay/June 2026" });
        Assert.Equal(("9709/12", "May/June", 2026), (exam.PaperCode, exam.Session, exam.Year));

        var footerOnly = PaperDetails.FromText(new[] { "Mark scheme cover without details", "© UCLES 2025 9709/32/O/N/25" });
        Assert.Equal(("9709/32", "October/November", 2025), (footerOnly.PaperCode, footerOnly.Session, footerOnly.Year));

        var ms = PaperDetails.FromText(new[] { "MATHEMATICS9709/03\nPaper 3 Pure Mathematics 3 For examination from 2027\nMARK SCHEME\nSpecimen" });
        Assert.True(ms.IsMarkScheme);
        var (conflicts, unreadable) = PaperDetails.Compare(real, ms);
        Assert.Equal(new[] { ("Component", "1", "3") }, conflicts);
        Assert.False(unreadable);
        Assert.True(PaperDetails.Compare(real, PaperDetails.FromText(new[] { "MARK SCHEME" })).Unreadable);
    }

    [Theory]
    [InlineData("10 (b) (ii)", "10(b)(ii)")]
    [InlineData("10b(ii)", "10(b)(ii)")]
    [InlineData("10bii", "10(b)(ii)")]
    [InlineData("Q3(a)", "3(a)")]
    [InlineData("7", "7")]
    [InlineData("6(i)", "6(i)")]
    public void Subpart_numbers_are_normalised(string printed, string expected) =>
        Assert.Equal(expected, CambridgeImportLogic.Normalise(printed));

    [Fact]
    public void A_lead_in_returned_as_its_own_part_goes_to_the_front_of_its_subparts()
    {
        var reading = CambridgeImportLogic.ParsePaper("""
        {"questions":[{"number":10,"stem":"The circle has centre C.","parts":[
          {"label":"(a)","text":"State C.","marks":1},
          {"label":"(b)","text":"It is given that D is the midpoint of AB.","marks":null},
          {"label":"(b)(i)","text":"Find the equation of AB.","marks":4},
          {"label":"(b)(ii)","text":"It is given that D is the midpoint of AB. Find A and B.","marks":3}]}]}
        """);
        var parts = CambridgeImportLogic.BuildParts(reading, new Dictionary<string, Guid>());
        Assert.Equal(new[] { "10(a)", "10(b)(i)", "10(b)(ii)" }, parts.Select(p => p.Number));
        Assert.Equal("It is given that D is the midpoint of AB. Find the equation of AB.", parts[1].Text);
        Assert.Equal("It is given that D is the midpoint of AB. Find A and B.", parts[2].Text);   // not repeated
        Assert.Equal(new[] { 1, 2, 3 }, parts.Select(p => p.Order));
    }

    // ── Step 1 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Paper_is_read_once_into_nested_parts_with_shared_stem_figure_and_suggested_topics()
    {
        var topics = await SeedSyllabusAsync();
        var svc = Svc();
        var i = await ReadPaperAsync(svc);

        Assert.Equal(("9709/12", "May/June", 2026), (i.PaperCode, i.Session, i.Year));
        Assert.NotNull(i.SyllabusVersionId);
        Assert.Equal(new[] { "2", "10(a)", "10(b)(i)", "10(b)(ii)", "12(a)", "12(b)", "12(c)" }, i.Parts.Select(p => p.Number));
        var b2 = i.Parts.Single(p => p.Number == "10(b)(ii)");
        Assert.Equal("The circle $x^{2} + y^{2} + 4x - 2y - 20 = 0$ has centre $C$.", b2.Stem);
        Assert.StartsWith("It is given that the midpoint", b2.Text);          // text between parts kept on each later part
        Assert.Equal(3, b2.Marks);
        // One figure for question 12, kept on every part of it.
        Assert.All(i.Parts.Where(p => p.QuestionNumber == 12), p => Assert.Equal("q12", p.FigureKey));
        Assert.Null(i.Parts.Single(p => p.Number == "2").Stem);
        // Topics: confident suggestions become links; a low-confidence one is left for the teacher.
        Assert.Equal(topics["1.3"], i.Parts.Single(p => p.Number == "10(a)").SyllabusTopicId);
        Assert.Equal("suggested", i.Parts.Single(p => p.Number == "10(a)").TopicSource);
        Assert.Null(i.Parts.Single(p => p.Number == "12(c)").SyllabusTopicId);
        Assert.Equal("none", i.Parts.Single(p => p.Number == "12(c)").TopicSource);

        // Reopening and changing a topic never reads the paper again.
        await svc.RunPaperAsync(i.Id);
        Assert.True(await svc.SetTopicAsync(i.Parts.Single(p => p.Number == "12(c)"), topics["1.8"]));
        Assert.Equal(1, _reader.PaperCalls);
        var again = (await Svc().LoadAsync(i.Id, Owner))!;
        Assert.Equal(("teacher", topics["1.8"]), (again.Parts.Single(p => p.Number == "12(c)").TopicSource, again.Parts.Single(p => p.Number == "12(c)").SyllabusTopicId));
        Assert.Equal(1, again.LastCompletedStep);
    }

    [Fact]
    public async Task Same_paper_again_opens_the_same_draft_but_another_teacher_gets_their_own()
    {
        var svc = Svc();
        var a = await svc.StartAsync(Owner, OwnerEmail, PaperPdf(), "p.pdf");
        var again = await svc.StartAsync(Owner, OwnerEmail, PaperPdf(), "copy.pdf");
        Assert.True(again.Existing);
        Assert.Equal(a.Import!.Id, again.Import!.Id);

        var other = await svc.StartAsync(Guid.NewGuid(), "other@school.test", PaperPdf(), "p.pdf");
        Assert.False(other.Existing);
        Assert.NotEqual(a.Import.Id, other.Import!.Id);
        Assert.Null(await svc.LoadAsync(a.Import.Id, other.Import.OwnerId));      // no access to someone else's draft

        Assert.Contains("Mark Scheme", (await svc.StartAsync(Owner, OwnerEmail, SchemePdf(), "ms.pdf")).Error);
    }

    [Fact]
    public async Task A_failed_reading_keeps_the_draft_and_a_retry_continues()
    {
        var svc = Svc();
        _reader.Fail = new AiCallException("unavailable", "down");
        var i = await ReadPaperAsync(svc);
        Assert.Equal(ImportStage.Failed, i.PaperStage);
        Assert.Contains("temporarily unavailable", i.PaperError);
        Assert.Empty(i.Parts);

        _reader.Fail = null;
        await svc.RunPaperAsync(i.Id);
        i = (await Svc().LoadAsync(i.Id, Owner))!;
        Assert.Equal(ImportStage.Done, i.PaperStage);
        Assert.Equal(7, i.Parts.Count);

        // The mark scheme fails too; the retry reads only the mark scheme, not the paper again.
        _reader.Fail = new AiCallException("timeout", "slow");
        await svc.AttachMarkSchemeAsync(i, SchemePdf(), "ms.pdf");
        await svc.RunMarkSchemeAsync(i.Id);
        i = (await Svc().LoadAsync(i.Id, Owner))!;
        Assert.Equal(ImportStage.Failed, i.MarkSchemeStage);
        _reader.Fail = null;
        await svc.RunMarkSchemeAsync(i.Id);
        i = (await Svc().LoadAsync(i.Id, Owner))!;
        Assert.Equal(ImportStage.Done, i.MarkSchemeStage);
        Assert.Equal((2, 2), (_reader.PaperCalls, _reader.SchemeCalls));
    }

    // ── Step 2 ───────────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_mark_scheme_for_another_paper_is_refused_before_any_matching()
    {
        var svc = Svc();
        var i = await ReadPaperAsync(svc);
        var check = await svc.AttachMarkSchemeAsync(i, SchemePdf(code: "9709/13"), "9709_s26_ms_13.pdf");
        Assert.Equal(new[] { ("Variant", "2", "3") }, check.Conflicts);
        Assert.False(svc.SchemeReadiness(i).Ready);
        Assert.Equal(0, _reader.SchemeCalls);

        var unreadable = await svc.AttachMarkSchemeAsync(i, TestPdf.Build("TEST DATA\nMARK SCHEME\n(no details)"), "scan.pdf");
        Assert.Empty(unreadable.Conflicts);
        Assert.True(svc.SchemeReadiness(i).NeedsConfirmation);
        await svc.ConfirmSchemeDetailsAsync(i);
        Assert.True(svc.SchemeReadiness(i).Ready);
    }

    [Fact]
    public void Mark_scheme_rows_are_matched_by_full_number_whatever_their_order()
    {
        var parts = new List<(string, int?)> { ("2", 4), ("10(a)", 1), ("10(b)(i)", 4), ("10(b)(ii)", 3), ("12(a)", 1), ("12(b)", 4), ("12(c)", 4) };
        var (m, unused) = CambridgeImportLogic.Match(parts, CambridgeImportLogic.ParseScheme(SchemeJson));
        CambridgeImportLogic.PartMatch Get(string n) => m.Single(x => x.Number == n);

        // Rows on two pages, given out of order, are joined.
        var b1 = Get("10(b)(i)");
        Assert.Equal(ImportPartStatus.Matched, b1.Status);
        Assert.Equal("$y = -7x + 12$", b1.AnswerKey);
        Assert.Equal((4, "8–9"), (b1.SchemeMarks, b1.Pages));
        Assert.Contains("M1  $y - 1.5 = -7(x - 1.5)$  — With gradient", b1.Criteria);

        // An alternative method is listed apart and not added to the main method's marks.
        var b2 = Get("10(b)(ii)");
        Assert.Equal((ImportPartStatus.Matched, 3), (b2.Status, b2.SchemeMarks));
        Assert.Contains("Alternative method 1 (instead of the method above):", b2.Criteria);

        Assert.Equal(ImportPartStatus.Matched, Get("12(a)").Status);
        Assert.Contains("Marks differ: paper [4], mark scheme 3.", Get("12(b)").Reason);
        Assert.Equal("No mark scheme row for 12(c).", Get("12(c)").Reason);
        Assert.Contains("could not be read", Get("2").Reason);
        Assert.Equal(new[] { "13(a)" }, unused);
    }

    [Fact]
    public void A_parent_row_shared_row_or_missing_final_answer_needs_review()
    {
        var scheme = CambridgeImportLogic.ParseScheme("""
        {"rows":[
          {"question":"5(b)","answer":"Stretch factor 3","codes":"B1","marks":1,"page":3},
          {"question":"6(a)(b)","appliesTo":["6(a)","6(b)"],"answer":"Expansion","codes":"B1 B1","marks":2,"final":true,"page":4},
          {"question":"7","answer":"Method","codes":"M1","marks":1,"page":5},
          {"question":"7","answer":"Another step","codes":"A1","marks":1,"page":5}
        ]}
        """);
        var (m, _) = CambridgeImportLogic.Match(new List<(string, int?)> { ("5(b)(i)", 1), ("5(b)(ii)", 2), ("6(a)", 1), ("6(b)", 1), ("7", 2) }, scheme);
        Assert.Equal("The mark scheme row 5(b) could belong to 5(b)(i) or 5(b)(ii).", m[0].Reason);
        Assert.Contains("One mark scheme row covers 6(a) and 6(b)", m[2].Reason);
        Assert.Contains("This row is shared with 6(b).", m[2].Criteria);
        Assert.Equal(ImportPartStatus.NeedsReview, m[4].Status);
        Assert.Null(m[4].AnswerKey);
        Assert.Contains("no single final answer", m[4].Reason);
    }

    // ── Step 3 and saving ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task A_new_reading_never_overwrites_the_teachers_version_without_a_choice()
    {
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        var part = i.Parts.Single(p => p.Number == "12(b)");
        Assert.Equal(ImportPartStatus.NeedsReview, part.Status);
        Assert.Null(await svc.EditAsync(part, "$b = \\frac{2}{3}$", "M1 A1 derivative = 0\nA1 b = 2/3\nB1 reject x = 2", 4));
        Assert.Null(await svc.ConfirmAsync(part));

        // A corrected mark scheme is uploaded and read: the edited part keeps the teacher's version and shows the new one.
        _reader.Scheme = SchemeJson.Replace("$b = \\\\frac{2}{3}$", "$b = 0.667$");
        await svc.AttachMarkSchemeAsync(i, TestPdf.Build("TEST DATA second copy\nMATHEMATICS 9709/12\nPaper 1 Pure Mathematics 1 May/June 2026\nMARK SCHEME"), "ms-v2.pdf");
        await svc.RunMarkSchemeAsync(i.Id);
        i = (await Svc().LoadAsync(i.Id, Owner))!;
        part = i.Parts.Single(p => p.Number == "12(b)");
        Assert.Equal("$b = \\frac{2}{3}$", part.AnswerKey);
        var pending = CambridgeImportService.PendingOf(part);
        Assert.Equal("$b = 0.667$", pending!.AnswerKey);
        Assert.NotNull(await svc.ConfirmAsync(part));   // confirming waits for the choice

        await svc.ResolvePendingAsync(part, useNew: false);
        Assert.Equal("$b = \\frac{2}{3}$", part.AnswerKey);
        Assert.Null(part.PendingReplacement);
        // Untouched parts simply took the new reading.
        Assert.Equal(ImportPartStatus.Matched, i.Parts.Single(p => p.Number == "10(a)").Status);
    }

    [Fact]
    public async Task Saving_part_then_the_rest_makes_no_duplicates_and_keeps_stem_figure_key_and_criteria()
    {
        var topics = await SeedSyllabusAsync();
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        await LibraryFileStorage.SaveFigureAsync(_env, i.Id, "q12", new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 });

        // Confirm the clear-cut rows in one go, then save only those.
        var matched = i.Parts.Where(p => p.Status == ImportPartStatus.Matched).Select(p => p.Id).ToList();
        Assert.Equal(4, await svc.ConfirmMatchedAsync(i, matched));
        var first = await svc.SaveConfirmedAsync(i);
        Assert.Equal((4, 0), (first.Saved, first.AlreadySaved));

        var q = await _factory.Context.Questions.SingleAsync(x => x.Title.StartsWith("State the value of"));
        Assert.Equal(("The diagram shows the curve $y = x(x-2)^{2}$.", 1, "$a = 2$"), (q.Stem, q.Marks, q.AnswerKey));
        Assert.Equal("B1  $a = 2$", q.MarkScheme);
        Assert.Equal(topics["1.7"], q.SyllabusTopicId);
        Assert.Equal("Mathematics", q.Subject);                                   // the teacher's subject, apart from the topic
        Assert.Equal(LibraryFileStorage.LibraryFigurePrefix + q.Id, q.ImagePath);
        Assert.NotNull(LibraryFileStorage.ReadLibraryFigure(_env, q.ImagePath));
        Assert.StartsWith("9709/12 · May/June 2026 · Q12(a)", q.SourceLabel);
        Assert.False(q.IsPublic);

        // Saving again (a second click) adds nothing.
        Assert.Equal((0, 0), ((await svc.SaveConfirmedAsync(i)).Saved, 0));
        // Later: the rest is reviewed and saved; the first four are not saved twice.
        foreach (var p in i.Parts.Where(p => p.Status == ImportPartStatus.NeedsReview))
        {
            await svc.EditAsync(p, p.AnswerKey ?? "see criteria", p.Criteria ?? "B1 teacher's criteria", p.Marks);
            Assert.Null(await svc.ConfirmAsync(p));
        }
        var second = await svc.SaveConfirmedAsync(i);
        Assert.Equal(3, second.Saved);
        Assert.Equal(7, await _factory.Context.Questions.CountAsync());

        // The same paper in a new import by the same teacher finds what is already in the library.
        await svc.DiscardAsync(i);
        var j = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        foreach (var p in j.Parts) { await svc.EditAsync(p, p.AnswerKey ?? "k", p.Criteria ?? "c", p.Marks); await svc.ConfirmAsync(p); }
        var third = await svc.SaveConfirmedAsync(j);
        Assert.Equal((0, 7), (third.Saved, third.AlreadySaved));
        Assert.Equal(7, await _factory.Context.Questions.CountAsync());
    }

    [Fact]
    public async Task Parts_added_to_an_assignment_share_one_question_with_the_stem_and_figure()
    {
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        await LibraryFileStorage.SaveFigureAsync(_env, i.Id, "q12", new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 });
        foreach (var p in i.Parts) { await svc.EditAsync(p, p.AnswerKey ?? "k", p.Criteria ?? "c", p.Marks); await svc.ConfirmAsync(p); }
        await svc.SaveConfirmedAsync(i);
        var q12 = await _factory.Context.Questions.Where(q => q.SourceGroupKey!.EndsWith("#12")).OrderBy(q => q.SourceKey).ToListAsync();

        var assignmentId = Guid.NewGuid();
        var list = new List<JsonElement>();
        Assert.Equal(1, await LibraryToAssignment.AddAsync(_env, assignmentId, list, new[] { q12[1] }));   // 12(b) first
        Assert.Equal(2, await LibraryToAssignment.AddAsync(_env, assignmentId, list, new[] { q12[0], q12[2] }));
        Assert.Equal(0, await LibraryToAssignment.AddAsync(_env, assignmentId, list, new[] { q12[0] }));     // already there

        var dtos = JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>(JsonSerializer.Serialize(list))!;
        Assert.Equal(4, dtos.Count);
        var header = dtos[0];
        Assert.Equal("The diagram shows the curve $y = x(x-2)^{2}$.", header.Title);
        Assert.StartsWith(QuestionAttachmentStorage.WebPrefix + "/" + assignmentId, header.ImagePath);
        Assert.NotNull(QuestionAttachmentStorage.ResolveFromWebPath(_env, header.ImagePath));
        Assert.All(dtos.Skip(1), d => Assert.Equal(header.Id, d.GroupId));
        var b = dtos.Single(d => d.Title.StartsWith("Calculate"));
        Assert.Equal((4, "$b = \\frac{2}{3}$"), (b.Marks, b.AnswerKey));
        Assert.Contains("M1 A1", b.MarkScheme);
        Assert.True(b.RequireWork);
    }
}
