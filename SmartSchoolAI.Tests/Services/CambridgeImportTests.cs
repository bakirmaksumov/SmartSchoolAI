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
        public int PaperCalls, SchemeCalls, TopicCalls;
        public string Paper = PaperJson, Scheme = SchemeJson, Topics = """{"topics":[]}""";
        public string? LastTopicList, LastPartList, LastPartsJson;
        public Func<Task>? DuringTopicCall;
        public Exception? Fail;
        public async Task<string> SuggestTopicsAsync(string partsJson, string topicList, Guid importId, CancellationToken ct = default)
        {
            TopicCalls++; LastPartsJson = partsJson; LastTopicList = topicList;
            if (DuringTopicCall is not null) await DuringTopicCall();
            if (Fail is not null) throw Fail;
            return Topics;
        }
        public Task<string> ReadPaperAsync(byte[] pdf, string topicList, Guid importId, CancellationToken ct = default)
        { PaperCalls++; LastTopicList = topicList; if (Fail is not null) throw Fail; return Task.FromResult(Paper); }
        public Task<string> ReadMarkSchemeAsync(byte[] pdf, string partList, Guid importId, CancellationToken ct = default)
        { SchemeCalls++; LastPartList = partList; if (Fail is not null) throw Fail; return Task.FromResult(Scheme); }
    }

    private readonly FakeReader _reader = new();
    private CambridgeImportService Svc() => new(_factory.Context, _env, _reader, NullLogger<CambridgeImportService>.Instance);

    private async Task<Dictionary<string, Guid>> SeedSyllabusAsync(int first = 2026, int last = 2027)
    {
        var program = await _factory.Context.SyllabusPrograms.FirstOrDefaultAsync(x => x.Code == "9709") ?? new SyllabusProgram { Code = "9709", Title = "Mathematics" };
        var v = new SyllabusVersion { Program = program, FirstExamYear = first, LastExamYear = last, SourceName = "test", SourceSha256 = "x" + first,
                                      Status = SyllabusVersionStatus.Published };
        var c = new SyllabusComponent { Number = 1, Title = "Pure Mathematics 1", SourcePage = 19 };
        var order = 0;
        foreach (var (n, t) in new[] { ("1.1", "Quadratics"), ("1.2", "Functions"), ("1.3", "Coordinate geometry"), ("1.5", "Trigonometry"),
                                       ("1.6", "Series"), ("1.7", "Differentiation"), ("1.8", "Integration") })
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
        var parts = CambridgeImportLogic.BuildParts(reading, new List<CambridgeImportLogic.TopicRef>());
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
    public async Task The_teacher_corrects_a_parts_wording_and_the_shared_stem_before_saving()
    {
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        var b = i.Parts.Single(p => p.Number == "12(b)");
        Assert.Null(await svc.EditAsync(b, b.AnswerKey, b.Criteria, b.Marks, text: "Calculate the exact value of $b$.", stem: "The diagram shows $y = x(x-2)^{2}$.", import: i));
        Assert.Equal("Calculate the exact value of $b$.", b.Text);
        Assert.All(i.Parts.Where(p => p.QuestionNumber == 12), p => Assert.Equal("The diagram shows $y = x(x-2)^{2}$.", p.Stem));   // every part of Q12
        Assert.Equal("The circle $x^{2} + y^{2} + 4x - 2y - 20 = 0$ has centre $C$.", i.Parts.Single(p => p.Number == "10(a)").Stem);
        Assert.Equal("The question text cannot be empty.", await svc.EditAsync(b, b.AnswerKey, b.Criteria, b.Marks, text: "  "));
        Assert.Null(await svc.ConfirmAsync(b));
        await svc.SaveConfirmedAsync(i);
        var q = await _factory.Context.Questions.SingleAsync();
        Assert.Equal(("Calculate the exact value of $b$.", "The diagram shows $y = x(x-2)^{2}$."), (q.Title, q.Stem));
        Assert.Equal("This part is already in your Content Library; edit it there.", await svc.EditAsync(b, "x", null, null, text: "changed"));
    }

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

    // ── Answer keys with more than one required result ───────────────────────────────────────────────────────────
    // Shaped like question 1(a) of the 2027 specimen Paper 1 mark scheme, where the real reading kept only the second
    // of two required results as the key: two independent B1 rows, only one of them marked final.
    private const string TwoResultsPaper = """
    {"questions":[{"number":1,"stem":"The points $A(0, 1)$, … and $E(2, 9)$ lie on the curve $y = f(x)$.","figure":null,"parts":[
      {"label":"(a)","text":"Complete the table to show the gradients of $CE$ and $DE$.","marks":2,"page":2,"kind":"ShortAnswer"},
      {"label":"(b)","text":"State what the values in the table indicate about the value of $f'(2)$.","marks":1,"page":2,"kind":"ShortAnswer"}]}]}
    """;
    private const string TwoResultsScheme = """
    {"rows":[
      {"question":"1(a)","appliesTo":["1(a)"],"answer":"Gradient of $CE = 2.5$","codes":"B1","marks":1,"final":false,"page":4},
      {"question":"1(a)","appliesTo":["1(a)"],"answer":"Gradient of $DE = 2.1$","codes":"B1","marks":1,"final":true,"page":4},
      {"question":"1(b)","appliesTo":["1(b)"],"answer":"$f'(2) = 2$","codes":"B1","marks":1,"final":true,"guidance":"Accept reasonable conclusion following their gradient","page":4}
    ]}
    """;

    [Fact]
    public void Two_required_results_with_only_one_in_the_key_need_review_and_name_the_missing_one()
    {
        var (m, _) = CambridgeImportLogic.Match(new List<(string, int?)> { ("1(a)", 2), ("1(b)", 1) }, CambridgeImportLogic.ParseScheme(TwoResultsScheme));
        var a = m[0];
        Assert.Equal(ImportPartStatus.NeedsReview, a.Status);
        Assert.Equal("Gradient of $DE = 2.1$", a.AnswerKey);                    // what was extracted, kept for the teacher to complete
        Assert.Contains("asks for 2 separate results", a.Reason);
        Assert.Contains("Missing: Gradient of $CE = 2.5$.", a.Reason);
        Assert.Equal(ImportPartStatus.Matched, m[1].Status);
    }

    [Fact]
    public void Two_required_results_both_marked_final_make_a_complete_key()
    {
        var scheme = CambridgeImportLogic.ParseScheme(TwoResultsScheme.Replace("\"final\":false", "\"final\":true"));
        var a = CambridgeImportLogic.Match(new List<(string, int?)> { ("1(a)", 2) }, scheme).Matches[0];
        Assert.Equal(ImportPartStatus.Matched, a.Status);
        Assert.Equal("Gradient of $CE = 2.5$; Gradient of $DE = 2.1$", a.AnswerKey);
        Assert.Null(a.Reason);
    }

    [Fact]
    public void Two_ways_to_one_answer_are_one_result_not_an_incomplete_key()
    {
        var scheme = CambridgeImportLogic.ParseScheme("""
        {"rows":[
          {"question":"3","answer":"$7 + (n-1)d = 84$ and $7 + (3n-1)d = 245$","codes":"B1","marks":1,"page":5},
          {"question":"3","answer":"Eliminate $d$","codes":"M1","marks":1,"page":5},
          {"question":"3","answer":"$n = 12$","codes":"A1","marks":1,"final":true,"page":5},
          {"question":"3","answer":"OR $2nd = 161$, so $n = 12$","codes":"M1 A1","marks":2,"final":true,"page":5},
          {"question":"3","answer":"Use $u_{3n} - u_{n}$","codes":"M1","marks":1,"alternative":"Alternative method 1","page":5},
          {"question":"3","answer":"$n = 12$","codes":"A1","marks":1,"final":true,"alternative":"Alternative method 1","page":5},
          {"question":"4","answer":"$f^{-1}(x) = \\frac{x-2}{3}$","codes":"B1","marks":1,"page":5},
          {"question":"4","answer":"$gf(x) = 4(3x+2) - 12$","codes":"B1","marks":1,"page":5},
          {"question":"4","answer":"Equate, $x = \\frac{2}{7}$","codes":"M1 A1","marks":2,"final":true,"page":5}
        ]}
        """);
        var (m, _) = CambridgeImportLogic.Match(new List<(string, int?)> { ("3", 3), ("4", 4) }, scheme);
        Assert.Equal((ImportPartStatus.Matched, "$n = 12$", (string?)null), (m[0].Status, m[0].AnswerKey, m[0].Reason));
        Assert.Equal(3, m[0].SchemeMarks);                                       // the "OR" row is another method, not added
        Assert.Contains("(instead of the method above)", m[0].Criteria);
        // B1 rows before a method mark are steps towards the one answer, not separate results.
        Assert.Equal((ImportPartStatus.Matched, (string?)null), (m[1].Status, m[1].Reason));
    }

    [Fact]
    public async Task After_completing_the_key_the_teacher_can_confirm_the_part()
    {
        _reader.Paper = TwoResultsPaper;
        _reader.Scheme = TwoResultsScheme;
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        var a = i.Parts.Single(p => p.Number == "1(a)");
        Assert.Equal(ImportPartStatus.NeedsReview, a.Status);
        Assert.Contains("Missing: Gradient of $CE = 2.5$", a.ReviewReason);

        Assert.Null(await svc.EditAsync(a, "Gradient of $CE = 2.5$; Gradient of $DE = 2.1$", a.Criteria, a.Marks));
        Assert.Null(await svc.ConfirmAsync(a));
        Assert.Equal((1, 0), ((await svc.SaveConfirmedAsync(i)).Saved, 0));
        var q = await _factory.Context.Questions.SingleAsync();
        Assert.Equal("Gradient of $CE = 2.5$; Gradient of $DE = 2.1$", q.AnswerKey);
    }

    // ── Drawn answers ────────────────────────────────────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Sketch the graph of $y = f^{-1}(x)$ on the diagram.", true, true, false)]
    [InlineData("Hence sketch the curve, showing where it meets the axes.", true, true, false)]
    [InlineData("On the diagram, shade the region $R$.", true, true, false)]
    [InlineData("Find the area of the shaded region.", false, false, false)]       // the figure belongs to the question
    [InlineData("Complete the table to show the gradients of $CE$ and $DE$.", false, false, false)]
    [InlineData("Sketch the graph of $y = |2x - 3|$.", false, true, true)]          // says sketch, read as written
    [InlineData("The diagram shows a sketch of the curve. Find the coordinates of $M$.", true, true, true)]  // read as drawn, text does not ask
    public void A_drawn_answer_is_told_apart_from_a_figure_in_the_question(string text, bool readAsDrawing, bool drawing, bool unclear)
    {
        var f = CambridgeImportLogic.AnswerFormat(text, readAsDrawing);
        Assert.Equal((drawing, unclear), (f.Drawing, f.Question is not null));
        if (unclear) Assert.StartsWith(CambridgeImportLogic.FormatReasonPrefix, f.Question);
    }

    private const string DrawingPaper = """
    {"questions":[{"number":8,"stem":"The diagram shows the graph of $y = f(x)$.","figure":{"page":3,"box":[0.1,0.1,0.9,0.5],"description":"The graph of y = f(x)"},"parts":[
      {"label":"(a)","text":"State the range of $f$.","marks":1,"page":3,"usesFigure":true,"kind":"ShortAnswer","needsDrawing":false},
      {"label":"(b)","text":"On the diagram, sketch the graph of $y = f^{-1}(x)$.","marks":2,"page":3,"usesFigure":true,"kind":"OpenText","needsDrawing":true},
      {"label":"(c)","text":"Sketch the graph of $y = f(x) + 1$ on the same diagram.","marks":1,"page":3,"usesFigure":true,"kind":"ShortAnswer","needsDrawing":false}]}]}
    """;
    private const string DrawingScheme = """
    {"rows":[
      {"question":"8(a)","answer":"$-1 \\le f(x) \\le 3$","codes":"B1","marks":1,"final":true,"page":6},
      {"question":"8(b)","answer":"Reflection of the curve in $y = x$","codes":"B1 B1","marks":2,"final":true,"page":6},
      {"question":"8(c)","answer":"Curve translated up by 1","codes":"B1","marks":1,"final":true,"page":6}
    ]}
    """;

    [Fact]
    public async Task A_drawn_answer_reaches_the_assignment_as_a_sketch_pad_question_and_an_unclear_one_waits_for_the_teacher()
    {
        _reader.Paper = DrawingPaper;
        _reader.Scheme = DrawingScheme;
        var svc = Svc();
        var i = await ReadSchemeAsync(svc, await ReadPaperAsync(svc));
        var a = i.Parts.Single(p => p.Number == "8(a)");
        var b = i.Parts.Single(p => p.Number == "8(b)");
        var c = i.Parts.Single(p => p.Number == "8(c)");
        Assert.Equal((false, ImportPartStatus.Matched), (a.NeedsDrawing, a.Status));   // uses the figure, answered in writing
        Assert.Equal((true, ImportPartStatus.Matched), (b.NeedsDrawing, b.Status));
        Assert.Null(b.FormatQuestion);

        // The wording asks for a sketch but the reading said written: the teacher chooses, and cannot confirm first.
        Assert.Equal(ImportPartStatus.NeedsReview, c.Status);
        Assert.NotNull(c.FormatQuestion);
        Assert.Equal(0, await svc.ConfirmMatchedAsync(i, new[] { c.Id }));
        Assert.Equal("Choose a drawn or a written answer first.", await svc.ConfirmAsync(c));
        Assert.Null(await svc.SetFormatAsync(c, drawing: true));
        Assert.Equal((ImportPartStatus.Matched, (string?)null), (c.Status, c.FormatQuestion));

        await LibraryFileStorage.SaveFigureAsync(_env, i.Id, "q8", new byte[] { 0x89, 0x50, 0x4E, 0x47, 9, 9 });
        Assert.Equal(3, await svc.ConfirmMatchedAsync(i, i.Parts.Select(p => p.Id).ToList()));
        await svc.SaveConfirmedAsync(i);
        var saved = await _factory.Context.Questions.OrderBy(q => q.SourceKey).ToListAsync();
        Assert.Equal(new[] { false, true, true }, saved.Select(q => q.RequireDrawing));
        Assert.Equal(QuestionType.OpenText, saved[2].Type);

        // In the assignment the drawn parts use the existing sketch pad, under the shared question with the figure.
        var list = new List<JsonElement>();
        await LibraryToAssignment.AddAsync(_env, Guid.NewGuid(), list, saved);
        var dtos = JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>(JsonSerializer.Serialize(list))!;
        Assert.Equal(4, dtos.Count);
        Assert.False(dtos[0].RequireDrawing);                                     // the shared question
        Assert.Equal(new[] { false, true, true }, dtos.Skip(1).Select(d => d.RequireDrawing));
        Assert.Equal(new[] { true, false, false }, dtos.Skip(1).Select(d => d.RequireWork));
    }

    // ── Topics ───────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly List<CambridgeImportLogic.TopicRef> P1Topics = new()
    {
        new(Guid.Parse("00000000-0000-0000-0000-000000000011"), "1.1", "Quadratics"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000012"), "1.2", "Functions"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000015"), "1.5", "Trigonometry"),
        new(Guid.Parse("00000000-0000-0000-0000-000000000017"), "1.7", "Differentiation"),
    };

    [Theory]
    [InlineData("1.1", "high", "1.1", null)]
    [InlineData("1.1 Quadratics", "high", "1.1", null)]
    [InlineData("1.1 — Quadratics", null, "1.1", null)]
    [InlineData("quadratics", "high", "1.1", null)]                                   // an unambiguous title alone
    [InlineData("1.5 Trigonometry", "high", "1.5", null)]
    [InlineData("1.1 Functions", "high", null, "mixes 1.1 Quadratics and 1.2 Functions")]   // code and title disagree
    [InlineData("1.1 Vectors", "high", null, "does not match 1.1 Quadratics")]
    [InlineData("4.2 Vectors", "high", null, "not a topic of this component")]          // another component's code
    [InlineData("Vectors", "high", null, "not a topic of this component")]
    [InlineData("1.7 Differentiation", "low", null, "Unsure: possibly 1.7 Differentiation")]
    [InlineData(null, null, null, "No topic was suggested.")]
    public void A_suggested_topic_links_only_to_a_topic_of_the_list(string? raw, string? confidence, string? number, string? note)
    {
        var (id, why) = CambridgeImportLogic.ResolveTopic(raw, confidence, P1Topics);
        Assert.Equal(number, P1Topics.FirstOrDefault(t => t.Id == id)?.Number);
        if (note is null) Assert.Null(why); else Assert.Contains(note, why);
    }

    // The cause found on a teacher's draft (2024 Paper 11): no 9709 syllabus was published when the paper was read,
    // and the edition lookup demanded one covering the paper's own year. The model was told there were no topics,
    // returned none, and nothing could be linked — later publishing changed nothing for the draft.
    private const string PastPaper2024 = """
    {"questions":[
      {"number":1,"stem":null,"figure":null,"parts":[
        {"label":"(a)","text":"Express $3y^2 - 12y - 15$ in the form $3(y+a)^2 + b$.","marks":2,"page":2,"topic":null,"topicConfidence":null},
        {"label":"(b)","text":"Hence find the exact solutions of the equation $3x^4 - 12x^2 - 15 = 0$.","marks":3,"page":2,"topic":null,"topicConfidence":null}]},
      {"number":4,"stem":"The equation of a curve is $y = f(x)$, where $f(x) = kx^{3}$. The curve passes through $(2, 1)$.","figure":null,"parts":[
        {"label":"(a)","text":"Find the value of $k$.","marks":1,"page":4,"topic":null,"topicConfidence":null},
        {"label":"(c)","text":"Deduce the value of $f'(2)$ using the values in the table.","marks":1,"page":4,"topic":null,"topicConfidence":null}]},
      {"number":5,"stem":null,"figure":null,"parts":[
        {"label":"(a)","text":"Prove the identity $\\frac{\\sin^2 x - \\cos x - 1}{1 + \\cos x} \\equiv -\\cos x$.","marks":2,"page":5,"topic":null,"topicConfidence":null},
        {"label":"(b)","text":"Hence solve the equation … for $0 < x < 2\\pi$.","marks":3,"page":5,"topic":null,"topicConfidence":null}]}
    ]}
    """;
    private static byte[] Paper2024() => PaperPdf("9709/11", "May/June 2024", "2024");

    private async Task<CambridgeImport> ReadWithoutSyllabusAsync(CambridgeImportService svc)
    {
        _reader.Paper = PastPaper2024;
        var i = await ReadPaperAsync(svc, Paper2024());
        Assert.Equal((2024, 1), (i.Year, i.Component));
        Assert.Null(i.SyllabusVersionId);
        Assert.Contains("no published syllabus topics", _reader.LastTopicList);
        Assert.All(i.Parts, p => Assert.Null(p.SyllabusTopicId));
        Assert.Equal("none", (await svc.TopicBasisAsync(i)).State);                    // one message, not one per part
        return i;
    }

    [Fact]
    public async Task A_past_paper_read_before_the_syllabus_was_published_gets_topics_from_the_nearest_edition_without_rereading()
    {
        var svc = Svc();
        var i = await ReadWithoutSyllabusAsync(svc);
        var topics = await SeedSyllabusAsync();                                         // published later: 2026–2027

        await svc.RefreshTopicsAsync(i);                                                // opening the draft
        Assert.NotNull(i.SyllabusVersionId);
        var basis = await svc.TopicBasisAsync(i);
        Assert.Equal(("ok", "2026–2027", false), (basis.State, basis.Years, basis.Exact));   // nearest edition; the paper stays 2024
        Assert.Equal(2024, i.Year);

        // Old draft: its stored reading names no topics, so "Suggest missing topics" asks once, from stored text only.
        _reader.Topics = """
        {"topics":[
          {"part":"1(a)","topic":"1.1 Quadratics","topicConfidence":"high"},
          {"part":"1(b)","topic":"1.1 Quadratics","topicConfidence":"high"},
          {"part":"4(a)","topic":"1.7 Differentiation","topicConfidence":"low"},
          {"part":"4(c)","topic":"1.7 Differentiation","topicConfidence":"high"},
          {"part":"5(a)","topic":"1.5 Trigonometry","topicConfidence":"high"},
          {"part":"5(b)","topic":"1.5 Functions","topicConfidence":"high"}
        ]}
        """;
        Assert.True(await svc.BeginTopicsAsync(i));
        await svc.RunTopicsAsync(i.Id);
        Assert.Equal((1, 1, 0), (_reader.PaperCalls, _reader.TopicCalls, _reader.SchemeCalls));   // no PDF read again
        Assert.Contains("1.1 Quadratics", _reader.LastTopicList);
        Assert.Contains("\"previousPart\":{\"number\":\"1(a)\"", _reader.LastPartsJson);          // "Hence" sees the part before
        Assert.Contains("passes through", _reader.LastPartsJson);                                // and the shared stem

        var j = (await Svc().LoadAsync(i.Id, Owner))!;
        Topic(j, "1(a)", topics["1.1"]); Topic(j, "1(b)", topics["1.1"]); Topic(j, "4(c)", topics["1.7"]); Topic(j, "5(a)", topics["1.5"]);
        var k = j.Parts.Single(p => p.Number == "4(a)");
        Assert.Null(k.SyllabusTopicId);
        Assert.Contains("Unsure: possibly 1.7 Differentiation", k.TopicNote);
        var b = j.Parts.Single(p => p.Number == "5(b)");
        Assert.Null(b.SyllabusTopicId);                                                  // code and title disagree: left to check
        Assert.Contains("mixes", b.TopicNote);
        Assert.Equal(ImportStage.Done, j.TopicStage);
    }

    private static void Topic(CambridgeImport i, string number, Guid expected)
    {
        var p = i.Parts.Single(x => x.Number == number);
        Assert.Equal(expected, p.SyllabusTopicId);
        Assert.Equal("classifier", p.TopicSource);
    }

    [Fact]
    public async Task Topics_the_model_named_before_the_syllabus_was_published_are_linked_once_it_is_with_no_AI_call()
    {
        var svc = Svc();
        _reader.Paper = PastPaper2024.Replace("\"marks\":2,\"page\":2,\"topic\":null,\"topicConfidence\":null", "\"marks\":2,\"page\":2,\"topic\":\"1.1 Quadratics\",\"topicConfidence\":\"high\"");
        var i = await ReadPaperAsync(svc, Paper2024());
        Assert.All(i.Parts, p => Assert.Null(p.SyllabusTopicId));
        var topics = await SeedSyllabusAsync(2023, 2025);                               // the paper's own edition
        await svc.RefreshTopicsAsync(i);
        Assert.True((await svc.TopicBasisAsync(i)).Exact);
        Assert.Equal(topics["1.1"], i.Parts.Single(p => p.Number == "1(a)").SyllabusTopicId);
        Assert.Equal((1, 0), (_reader.PaperCalls, _reader.TopicCalls));
    }

    [Fact]
    public async Task The_teachers_choice_and_removal_are_kept_also_when_made_while_topics_are_being_suggested()
    {
        var svc = Svc();
        var i = await ReadWithoutSyllabusAsync(svc);
        var topics = await SeedSyllabusAsync();
        await svc.RefreshTopicsAsync(i);
        Assert.True(await svc.SetTopicAsync(i.Parts.Single(p => p.Number == "1(a)"), topics["1.2"]));   // the teacher's topic
        Assert.True(await svc.SetTopicAsync(i.Parts.Single(p => p.Number == "1(b)"), null));             // deliberately none
        _reader.DuringTopicCall = async () =>
        {
            // Meanwhile, in another request, the teacher picks a topic for 5(a).
            await using var db = _factory.NewContext();
            var other = new CambridgeImportService(db, _env, _reader, NullLogger<CambridgeImportService>.Instance);
            var mine = (await other.LoadAsync(i.Id, Owner))!;
            Assert.True(await other.SetTopicAsync(mine.Parts.Single(p => p.Number == "5(a)"), topics["1.8"]));
        };
        _reader.Topics = """
        {"topics":[{"part":"1(a)","topic":"1.1 Quadratics","topicConfidence":"high"},{"part":"1(b)","topic":"1.1","topicConfidence":"high"},
                   {"part":"5(a)","topic":"1.5","topicConfidence":"high"},{"part":"5(b)","topic":"1.5","topicConfidence":"high"}]}
        """;
        Assert.True(await svc.BeginTopicsAsync(i));
        Assert.DoesNotContain("\"part\":\"1(a)\"", _reader.LastPartsJson ?? "");
        await svc.RunTopicsAsync(i.Id);
        Assert.DoesNotContain("\"part\":\"1(a)\"", _reader.LastPartsJson);              // decided parts are not even asked about

        var j = (await Svc().LoadAsync(i.Id, Owner))!;
        Assert.Equal(topics["1.2"], j.Parts.Single(p => p.Number == "1(a)").SyllabusTopicId);
        Assert.Equal((null, "teacher"), (j.Parts.Single(p => p.Number == "1(b)").SyllabusTopicId, j.Parts.Single(p => p.Number == "1(b)").TopicSource));
        Assert.Equal((topics["1.8"], "teacher"), (j.Parts.Single(p => p.Number == "5(a)").SyllabusTopicId, j.Parts.Single(p => p.Number == "5(a)").TopicSource));
        Assert.Equal((topics["1.5"], "classifier"), (j.Parts.Single(p => p.Number == "5(b)").SyllabusTopicId, j.Parts.Single(p => p.Number == "5(b)").TopicSource));
    }

    [Fact]
    public async Task The_mark_scheme_reading_is_a_second_chance_for_parts_without_a_confident_topic()
    {
        var topics = await SeedSyllabusAsync();
        var svc = Svc();
        var i = await ReadPaperAsync(svc);
        Assert.Null(i.Parts.Single(p => p.Number == "12(c)").SyllabusTopicId);             // "low" on the paper
        _reader.Scheme = SchemeJson.Replace("{\"rows\":[", "{\"topics\":[{\"part\":\"12(c)\",\"topic\":\"1.8 Integration\",\"topicConfidence\":\"high\"},{\"part\":\"10(a)\",\"topic\":\"1.1\",\"topicConfidence\":\"high\"}],\"rows\":[");
        i = await ReadSchemeAsync(svc, i);
        Assert.Contains("SUBPARTS WITHOUT A CONFIDENT TOPIC", _reader.LastPartList);
        Assert.Contains("12(c): The diagram shows", _reader.LastPartList);
        Assert.DoesNotContain("10(a): ", _reader.LastPartList);                           // already has a topic
        Assert.Contains("1.8 Integration", _reader.LastPartList);
        Assert.Equal((topics["1.8"], "scheme"), (i.Parts.Single(p => p.Number == "12(c)").SyllabusTopicId, i.Parts.Single(p => p.Number == "12(c)").TopicSource));
        Assert.Equal((topics["1.3"], "suggested"), (i.Parts.Single(p => p.Number == "10(a)").SyllabusTopicId, i.Parts.Single(p => p.Number == "10(a)").TopicSource));
        Assert.Equal(1, _reader.SchemeCalls);
    }

    [Fact]
    public async Task Missing_year_or_component_asks_for_the_details_once()
    {
        await SeedSyllabusAsync();
        var svc = Svc();
        _reader.Paper = PastPaper2024;
        var i = await ReadPaperAsync(svc, TestPdf.Build("TEST DATA\nA paper without readable details", "1 (a) Express ..."));
        Assert.Equal("details", (await svc.TopicBasisAsync(i)).State);
        Assert.False(await svc.BeginTopicsAsync(i));
        await svc.SaveDetailsAsync(i, 1, "1", "May/June", 2024, "Mathematics");
        Assert.Equal("ok", (await svc.TopicBasisAsync(i)).State);
    }

    [Fact]
    public async Task A_part_saved_before_it_had_a_topic_fills_its_library_question_only_while_untouched()
    {
        var svc = Svc();
        var i = await ReadWithoutSyllabusAsync(svc);
        foreach (var p in i.Parts) { await svc.EditAsync(p, "k", "B1 c", p.Marks); await svc.ConfirmAsync(p); }
        await svc.SaveConfirmedAsync(i);
        var edited = await _factory.Context.Questions.SingleAsync(q => q.SourceKey!.EndsWith(":5(a)"));
        edited.UpdatedAt = DateTime.UtcNow;                                             // the teacher edited it in the library
        await _factory.Context.SaveChangesAsync();
        var topics = await SeedSyllabusAsync();
        await svc.RefreshTopicsAsync(i);
        _reader.Topics = """{"topics":[{"part":"1(a)","topic":"1.1","topicConfidence":"high"},{"part":"5(a)","topic":"1.5","topicConfidence":"high"}]}""";
        Assert.True(await svc.BeginTopicsAsync(i));
        await svc.RunTopicsAsync(i.Id);
        var q = await _factory.NewContext().Questions.ToDictionaryAsync(x => x.SourceKey![(x.SourceKey!.LastIndexOf(':') + 1)..]);
        Assert.Equal(topics["1.1"], q["1(a)"].SyllabusTopicId);
        Assert.Null(q["5(a)"].SyllabusTopicId);
    }
}
