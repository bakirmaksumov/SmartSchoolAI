using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SmartSchoolAI.Controllers;
using SmartSchoolAI.Models;
using SmartSchoolAI.Services;
using SmartSchoolAI.Tests.TestHelpers;
using Xunit;

namespace SmartSchoolAI.Tests.Services;

/// <summary>
/// The syllabus library: reading a syllabus PDF, importing it once, publishing it, and teachers linking questions
/// to its topics and filtering by them. The PDFs here are generated TEST DATA shaped like a Cambridge syllabus
/// (cover page, contents, overview, Subject content) — not Cambridge's document.
/// </summary>
public class SyllabusTests : IDisposable
{
    private readonly TestDbContextFactory _factory = new();
    private readonly FakeWebHostEnvironment _env = new();
    public void Dispose() { _factory.Dispose(); _env.Dispose(); }

    // ── A small syllabus PDF in the real layout, clearly marked as test data ────────────────────────────────────
    private static readonly string[] TestPages =
    {
        "TEST DATA — not a real syllabus\nCambridge International\nAS & A Level\nMathematics 9709\nUse this syllabus for exams in 2026 and 2027.\nVersion 5",
        "Contents\n3 Subject content ........ 18\n1 Pure Mathematics 1 (for Paper 1) ........ 18\n2 Pure Mathematics 2 (for Paper 2) ........ 22",
        "Content overview\n1.1 Quadratics 1.2 Functions\n2.1 Algebra",
        "3 Subject content\nThe mathematical content for each component is detailed below.\n1 Pure Mathematics 1 (for Paper 1)\n1.1 Quadratics\nCandidates should be able to:\n• carry out the process of completing the square\n1.2 Trigonometry Candidates should be able to: • sketch graphs",
        "Cambridge International AS & A Level Mathematics 9709 syllabus for 2026 and 2027. Subject content\n2 Pure Mathematics 2 (for Paper 2)\n2.1 Algebra\nCandidates should be able to:\n• use the factor theorem, e.g. 2.5 times\n2.2 Trigonometry",
        "4 Details of the assessment\n5.1 Not a topic",
    };

    /// <summary>A minimal text-only PDF, one string per page (lines split on '\n'). Written by hand: iText needs an extra
    /// package to write PDFs, and the app only ever reads them.</summary>
    private static byte[] Pdf(params string[] pages)
    {
        static string Esc(string t) => t.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)")
            .Replace("\u2022", "-").Replace("\u2014", "-").Replace("\u2013", "-");
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = new System.Text.StringBuilder("BT /F1 10 Tf 14 TL 40 800 Td\n");
            foreach (var line in page.Split('\n')) content.Append('(').Append(Esc(line)).Append(") Tj T*\n");
            content.Append("ET");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count} 0 R >>");
            kids.Add(objects.Count);
        }
        objects[1] = $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] >>";

        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Parser_reads_code_years_components_and_topic_headings_only()
    {
        var s = SyllabusPdfParser.Parse(Pdf(TestPages));

        Assert.Equal("9709", s.Code);
        Assert.Equal((2026, 2027), (s.FirstYear!.Value, s.LastYear!.Value));
        Assert.Equal("Version 5", s.DocumentVersion);
        Assert.Empty(s.Problems);
        Assert.Equal(new[] { "1 Pure Mathematics 1", "2 Pure Mathematics 2" }, s.Components.Select(c => $"{c.Number} {c.Title}"));
        // Headings only: no learning objective, nothing from the contents/overview pages or after the section.
        Assert.Equal(new[] { "1.1 Quadratics p4", "1.2 Trigonometry p4" }, s.Components[0].Topics.Select(t => $"{t.Number} {t.Title} p{t.Page}"));
        Assert.Equal(new[] { "2.1 Algebra p5", "2.2 Trigonometry p5" }, s.Components[1].Topics.Select(t => $"{t.Number} {t.Title} p{t.Page}"));
    }

    [Fact]
    public void A_file_that_is_not_a_syllabus_reports_why()
    {
        var s = SyllabusPdfParser.Parse(Pdf("Hello", "World"));
        Assert.Null(s.Code);
        Assert.NotEmpty(s.Problems);
        Assert.Single(SyllabusPdfParser.Parse(new byte[] { 1, 2, 3 }).Problems);
    }

    private async Task<SyllabusService.CatalogVersion> PublishedAsync(SyllabusService svc)
    {
        var r = await svc.ImportAsync(Pdf(TestPages), "test-9709.pdf", null, null);
        await svc.PublishAsync(r.Version!.Id);
        return (await svc.CatalogAsync()).Single();
    }

    [Fact]
    public async Task Importing_the_same_file_again_adds_nothing_and_drafts_stay_hidden_until_published()
    {
        var svc = new SyllabusService(_factory.Context);
        var bytes = Pdf(TestPages);

        var first = await svc.ImportAsync(bytes, "a.pdf", "https://example.test/9709.pdf", null);
        Assert.False(first.AlreadyInLibrary);
        Assert.Equal(SyllabusVersionStatus.Draft, first.Version!.Status);
        Assert.Empty(await svc.CatalogAsync());                        // a draft is not offered to teachers

        var again = await svc.ImportAsync(bytes, "a-copy.pdf", null, null);
        Assert.True(again.AlreadyInLibrary);
        Assert.Equal(first.Version.Id, again.Version!.Id);
        Assert.Equal(1, await _factory.Context.SyllabusVersions.CountAsync());
        Assert.Equal(4, await _factory.Context.SyllabusTopics.CountAsync());

        await svc.PublishAsync(first.Version.Id);
        var v = Assert.Single(await svc.CatalogAsync());
        Assert.Equal("9709 · 2026–2027", v.Label);
    }

    [Fact]
    public async Task A_different_file_for_the_same_exam_years_is_reported_and_changes_nothing()
    {
        var svc = new SyllabusService(_factory.Context);
        var published = await PublishedAsync(svc);
        var quadratics = published.Components[0].Topics.Single(t => t.Title == "Quadratics");
        var q = new Question { Title = "Linked", AnswerKey = "1", CreatedByEmail = "t@school.test", SyllabusTopicId = quadratics.Id };
        _factory.Context.Questions.Add(q);
        await _factory.Context.SaveChangesAsync();

        // Another edition of the same 2026–2027 syllabus: a later document version with one more topic.
        var revised = TestPages.ToArray();
        revised[0] = revised[0].Replace("Version 5", "Version 6");
        revised[3] = revised[3].Replace("1.2 Trigonometry", "1.2 Functions\nCandidates should be able to:\n1.3 Trigonometry");
        var r = await svc.ImportAsync(Pdf(revised), "9709-revised.pdf", null, null);

        Assert.Equal(SyllabusService.ImportOutcome.DifferentFileSameYears, r.Outcome);
        Assert.True(r.AlreadyInLibrary);
        Assert.Equal(published.Id, r.Version!.Id);                          // the stored edition is the one returned
        Assert.Equal("Version 5", r.Version.DocumentVersion);
        Assert.Equal(SyllabusVersionStatus.Published, r.Version.Status);
        Assert.NotNull(r.Uploaded);
        Assert.Equal(("9709-revised.pdf", "Version 6", 2, 5), (r.Uploaded!.Name, r.Uploaded.DocumentVersion, r.Uploaded.Components, r.Uploaded.Topics));
        Assert.NotEqual(r.Version.SourceSha256, r.Uploaded.Sha256);

        // Nothing added or replaced: one version, the same published topics, and the question still linked.
        Assert.Equal(1, await _factory.Context.SyllabusVersions.CountAsync());
        Assert.Equal(4, await _factory.Context.SyllabusTopics.CountAsync());
        Assert.Equal(published.Components.SelectMany(c => c.Topics).Select(t => t.Id),
                     (await svc.CatalogAsync()).Single().Components.SelectMany(c => c.Topics).Select(t => t.Id));
        Assert.Equal(quadratics.Id, (await _factory.Context.Questions.AsNoTracking().SingleAsync(x => x.Id == q.Id)).SyllabusTopicId);

        // And the very same file again is still "same file", not "different".
        var again = await svc.ImportAsync(Pdf(TestPages), "copy.pdf", null, null);
        Assert.Equal(SyllabusService.ImportOutcome.SameFile, again.Outcome);
        Assert.Null(again.Uploaded);
    }

    [Fact]
    public async Task A_new_edition_sits_beside_the_previous_one()
    {
        var svc = new SyllabusService(_factory.Context);
        await PublishedAsync(svc);
        var older = TestPages.ToArray();
        older[0] = older[0].Replace("exams in 2026 and 2027", "examination in June and November 2023, 2024 and 2025");
        var r = await svc.ImportAsync(Pdf(older), "older.pdf", null, null);
        await svc.PublishAsync(r.Version!.Id);

        Assert.Equal(new[] { "9709 · 2026–2027", "9709 · 2023–2025" }, (await svc.CatalogAsync()).Select(v => v.Label));
        Assert.Equal(1, await _factory.Context.SyllabusPrograms.CountAsync());
    }

    [Fact]
    public async Task Same_named_topics_in_different_components_are_separate_and_a_title_fix_keeps_links()
    {
        var svc = new SyllabusService(_factory.Context);
        var v = await PublishedAsync(svc);
        var trig = v.Components.SelectMany(c => c.Topics.Where(t => t.Title == "Trigonometry").Select(t => (c, t))).ToList();
        Assert.Equal(2, trig.Count);
        Assert.NotEqual(trig[0].t.Id, trig[1].t.Id);

        var q = new Question { Title = "Solve", AnswerKey = "1", CreatedByEmail = "t@x", SyllabusTopicId = trig[1].t.Id };
        _factory.Context.Questions.Add(q);
        await _factory.Context.SaveChangesAsync();

        // A linked topic cannot be removed; its title can be corrected and the link follows.
        var err = await svc.SaveEditsAsync(v.Id, new[] { new SyllabusService.TopicEdit(trig[1].t.Id, "2.2", "Trigonometry", 5, true) }, Array.Empty<SyllabusService.TopicAddition>());
        Assert.NotNull(err);
        Assert.Null(await svc.SaveEditsAsync(v.Id, new[] { new SyllabusService.TopicEdit(trig[1].t.Id, "2.2", "Trigonometry (corrected)", 5, false) }, Array.Empty<SyllabusService.TopicAddition>()));
        var label = (await svc.LabelsAsync(new[] { q.SyllabusTopicId!.Value }))[q.SyllabusTopicId.Value];
        Assert.Equal("9709 · 2026–2027 · Pure Mathematics 2 · Trigonometry (corrected)", label.Full);
        Assert.Equal("9709 · P2 · Trigonometry (corrected)", label.Short);
    }

    // ── Teachers: link, edit, remove, filter, add to an assignment ─────────────────────────────────────────────

    private async Task<User> TeacherAsync(string email)
    {
        var role = await _factory.Context.Roles.FirstAsync(r => r.Name == Role.Teacher);
        var u = new User { Email = email, PasswordHash = "x", FullName = "T", RoleId = role.Id };
        _factory.Context.Users.Add(u);
        await _factory.Context.SaveChangesAsync();
        return u;
    }

    private ContentLibraryController Library(User t, IDictionary<string, string>? form = null)
    {
        var c = new ContentLibraryController(_factory.Context, _env);
        ControllerTestContext.SignIn(c, t.Id, t.Email, form: form);
        return c;
    }

    [Fact]
    public async Task Teacher_links_a_question_and_removing_the_link_keeps_everything_else()
    {
        var v = await PublishedAsync(new SyllabusService(_factory.Context));
        var quadratics = v.Components[0].Topics.Single(t => t.Title == "Quadratics");
        var t = await TeacherAsync("teacher@school.test");

        await Library(t, new Dictionary<string, string>()).Create("Solve x^2 = 9", QuestionType.ShortAnswer, DifficultyLevel.Medium, "My algebra topic", 10,
            "3", null, false, null, null, null, quadratics.Id);
        var q = await _factory.Context.Questions.SingleAsync();
        Assert.Equal(quadratics.Id, q.SyllabusTopicId);
        Assert.Equal("My algebra topic", q.Subject);                          // the teacher's own topic, kept separately

        // An Edit that leaves the link as it is keeps it.
        var form = new Dictionary<string, string> { ["syllabusTopicId"] = quadratics.Id.ToString() };
        await Library(t, form).Edit(q.Id, q.Title, q.Type, q.Difficulty, q.Subject, q.GradeLevel, q.AnswerKey, null, false, null, null, quadratics.Id);
        Assert.Equal(quadratics.Id, (await _factory.Context.Questions.AsNoTracking().SingleAsync()).SyllabusTopicId);

        // "Remove link": the picker posts an empty value; text, answer and own topic stay.
        form["syllabusTopicId"] = "";
        await Library(t, form).Edit(q.Id, q.Title, q.Type, q.Difficulty, q.Subject, q.GradeLevel, q.AnswerKey, null, false, null, null, null);
        var after = await _factory.Context.Questions.AsNoTracking().SingleAsync();
        Assert.Null(after.SyllabusTopicId);
        Assert.Equal(("Solve x^2 = 9", "3", "My algebra topic"), (after.Title, after.AnswerKey, after.Subject));
    }

    [Fact]
    public async Task An_older_question_edited_without_the_field_keeps_its_link_and_a_draft_topic_cannot_be_linked()
    {
        var svc = new SyllabusService(_factory.Context);
        var v = await PublishedAsync(svc);
        var topic = v.Components[0].Topics[0];
        var t = await TeacherAsync("teacher@school.test");
        var q = new Question { Title = "Old", AnswerKey = "1", CreatedByEmail = t.Email, Subject = "Quadratics", SyllabusTopicId = topic.Id };
        _factory.Context.Questions.Add(q);
        await _factory.Context.SaveChangesAsync();

        await Library(t, new Dictionary<string, string>()).Edit(q.Id, "Old (edited)", q.Type, q.Difficulty, q.Subject, null, "1", null, false, null, null);
        Assert.Equal(topic.Id, (await _factory.Context.Questions.AsNoTracking().SingleAsync()).SyllabusTopicId);

        var older = TestPages.ToArray();
        older[0] = older[0].Replace("2026 and 2027", "2024 and 2025");
        var draft = await svc.ImportAsync(Pdf(older), "draft.pdf", null, null);
        var draftTopic = draft.Version!.Components.First().Topics.First().Id;
        await Library(t, new Dictionary<string, string>()).Create("New", QuestionType.ShortAnswer, DifficultyLevel.Easy, null, null, "1", null, false, null, null, null, draftTopic);
        Assert.Null((await _factory.Context.Questions.SingleAsync(x => x.Title == "New")).SyllabusTopicId);
    }

    [Fact]
    public async Task Library_filters_by_version_component_and_topic_and_ignores_a_free_text_subject()
    {
        var v = await PublishedAsync(new SyllabusService(_factory.Context));
        var p1 = v.Components[0];
        var quadratics = p1.Topics.Single(x => x.Title == "Quadratics");
        var algebra = v.Components[1].Topics.Single(x => x.Title == "Algebra");
        var t = await TeacherAsync("teacher@school.test");
        var other = await TeacherAsync("other@school.test");
        _factory.Context.Questions.AddRange(
            new Question { Title = "Linked quadratic", AnswerKey = "1", CreatedByEmail = t.Email, SyllabusTopicId = quadratics.Id },
            new Question { Title = "Linked algebra", AnswerKey = "1", CreatedByEmail = t.Email, SyllabusTopicId = algebra.Id },
            new Question { Title = "Typed Quadratics only", AnswerKey = "1", CreatedByEmail = t.Email, Subject = "Quadratics" },
            new Question { Title = "Someone else's private one", AnswerKey = "1", CreatedByEmail = other.Email, SyllabusTopicId = quadratics.Id });
        await _factory.Context.SaveChangesAsync();

        async Task<string[]> Titles(Guid? ver = null, Guid? comp = null, Guid? topic = null, string? search = null)
        {
            var r = (ViewResult)await Library(t).Index(search: search, syllabusVersion: ver, syllabusComponent: comp, syllabusTopic: topic);
            return ((List<Question>)r.Model!).Select(q => q.Title).OrderBy(x => x).ToArray();
        }

        Assert.Equal(new[] { "Linked quadratic" }, await Titles(topic: quadratics.Id));
        Assert.Equal(new[] { "Linked quadratic" }, await Titles(comp: p1.Id));
        Assert.Equal(new[] { "Linked algebra", "Linked quadratic" }, await Titles(ver: v.Id));
        Assert.Empty(await Titles(topic: quadratics.Id, search: "algebra"));           // combines with search
        Assert.Equal(3, (await Titles()).Length);                                      // no filter: the usual list

        var labels = (Dictionary<Guid, SyllabusService.TopicLabel>)((ViewResult)await Library(t).Index()).ViewData["SyllabusLabels"]!;
        Assert.Equal("9709 · P1 · Quadratics", labels[quadratics.Id].Short);
    }

    [Fact]
    public async Task Adding_a_linked_question_to_an_assignment_keeps_the_link()
    {
        var v = await PublishedAsync(new SyllabusService(_factory.Context));
        var topic = v.Components[0].Topics[0];
        var t = await TeacherAsync("teacher@school.test");
        var cls = new Class { Name = "A", Subject = "Math", AcademicYear = "2026", InviteCode = "ABC123", TeacherId = t.Id };
        _factory.Context.Classes.Add(cls);
        var a = new Assignment { Title = "Mixed", TeacherId = t.Id, ClassId = cls.Id };
        _factory.Context.Assignments.Add(a);
        var linked = new Question { Title = "Cambridge one", AnswerKey = "3", CreatedByEmail = t.Email, SyllabusTopicId = topic.Id };
        var plain = new Question { Title = "Ordinary one", AnswerKey = "4", CreatedByEmail = t.Email, Subject = "Fractions" };
        _factory.Context.Questions.AddRange(linked, plain);
        await _factory.Context.SaveChangesAsync();

        await Library(t).AttachToAssignment(linked.Id, a.Id);
        await Library(t).AttachToAssignment(plain.Id, a.Id);

        var qs = JsonSerializer.Deserialize<List<SmartSchoolAI.Models.Dtos.QuestionDto>>((await _factory.Context.Assignments.AsNoTracking().SingleAsync()).Questions!)!;
        Assert.Equal(topic.Id, qs[0].SyllabusTopicId);
        Assert.Null(qs[1].SyllabusTopicId);
    }
}
