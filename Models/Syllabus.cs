namespace SmartSchoolAI.Models;

/// <summary>
/// An exam board programme a question can be aligned to, e.g. Cambridge International AS &amp; A Level Mathematics (9709).
/// One row per subject code; the actual topic lists live in its <see cref="SyllabusVersion"/>s.
/// </summary>
public class SyllabusProgram
{
    public Guid   Id    { get; set; } = Guid.NewGuid();
    public string Code  { get; set; } = string.Empty;   // "9709"
    public string Title { get; set; } = string.Empty;   // "Cambridge International AS & A Level Mathematics"

    public ICollection<SyllabusVersion> Versions { get; set; } = new List<SyllabusVersion>();
}

public enum SyllabusVersionStatus
{
    /// <summary>Imported, waiting for an administrator to check it. Teachers do not see it.</summary>
    Draft     = 0,
    /// <summary>Checked and published: teachers can link questions to its topics.</summary>
    Published = 1
}

/// <summary>
/// One edition of a syllabus ("for exams in 2026 and 2027"). Editions sit side by side: a new one never replaces an
/// older one, because questions stay linked to the topic they were written for.
/// </summary>
public class SyllabusVersion
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   ProgramId { get; set; }
    public int    FirstExamYear { get; set; }
    public int    LastExamYear  { get; set; }

    /// <summary>The document's own edition label, e.g. "Version 5", when the title page gives one.</summary>
    public string? DocumentVersion { get; set; }

    /// <summary>Where the topics came from: the uploaded file's name and, when known, its public address.</summary>
    public string  SourceName { get; set; } = string.Empty;
    public string? SourceUrl  { get; set; }

    /// <summary>SHA-256 of the uploaded file, so importing the same file again finds this version instead of copying it.</summary>
    public string  SourceSha256 { get; set; } = string.Empty;

    public SyllabusVersionStatus Status { get; set; } = SyllabusVersionStatus.Draft;
    public DateTime  ImportedAt  { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public Guid?     ImportedById { get; set; }

    public SyllabusProgram Program { get; set; } = null!;
    public ICollection<SyllabusComponent> Components { get; set; } = new List<SyllabusComponent>();

    /// <summary>"2026–2027", or "2026" for a single year.</summary>
    public string Years => FirstExamYear == LastExamYear ? $"{FirstExamYear}" : $"{FirstExamYear}–{LastExamYear}";
}

/// <summary>A component of the syllabus: one exam paper, e.g. 1 · Pure Mathematics 1.</summary>
public class SyllabusComponent
{
    public Guid   Id        { get; set; } = Guid.NewGuid();
    public Guid   VersionId { get; set; }
    public int    Number    { get; set; }                  // the paper number
    public string Title     { get; set; } = string.Empty;  // official English name
    public int?   SourcePage { get; set; }                 // page of the PDF where it starts

    public SyllabusVersion Version { get; set; } = null!;
    public ICollection<SyllabusTopic> Topics { get; set; } = new List<SyllabusTopic>();
}

/// <summary>
/// A topic heading from the syllabus's Subject content, e.g. 1.1 Quadratics. Questions link to it by <see cref="Id"/>,
/// so a typo in <see cref="Title"/> can be corrected without losing any link. Topics with the same name in different
/// components (Trigonometry in Papers 1, 2 and 3) are separate rows.
/// </summary>
public class SyllabusTopic
{
    public Guid    Id          { get; set; } = Guid.NewGuid();
    public Guid    ComponentId { get; set; }
    public string? Number      { get; set; }                  // "1.1", as printed
    public string  Title       { get; set; } = string.Empty;  // official English title
    public int     Order       { get; set; }
    public int?    SourcePage  { get; set; }                  // page of the PDF (1 = the cover)

    public SyllabusComponent Component { get; set; } = null!;
}
