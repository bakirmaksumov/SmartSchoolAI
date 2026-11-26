namespace SmartSchoolAI.Models;

/// <summary>Where a step's background reading stands. Results of a finished stage are kept, so opening a page or
/// changing a topic never starts the (paid) reading again; a failed stage can be retried on its own.</summary>
public enum ImportStage
{
    NotStarted = 0,
    Running    = 1,
    Done       = 2,
    Failed     = 3,
}

/// <summary>The review state of one subpart of an imported paper.</summary>
public enum ImportPartStatus
{
    /// <summary>Read from the Question Paper; no mark scheme yet.</summary>
    Recognised  = 0,
    /// <summary>The mark scheme matched one-to-one. Still needs the teacher's confirmation.</summary>
    Matched     = 1,
    /// <summary>Something needs the teacher: see <see cref="CambridgeImportPart.ReviewReason"/>.</summary>
    NeedsReview = 2,
    /// <summary>The teacher confirmed it; ready to save.</summary>
    Confirmed   = 3,
    /// <summary>Saved to the teacher's Content Library (<see cref="CambridgeImportPart.SavedQuestionId"/>).</summary>
    Saved       = 4,
}

/// <summary>
/// A teacher's step-by-step import of one Cambridge International AS &amp; A Level Mathematics (9709) paper: the
/// Question Paper, later its Mark Scheme, the reviewed answer keys and what was saved to the Content Library.
/// A draft belongs to the teacher who started it; nobody else can open it, its PDFs or its keys.
/// </summary>
public class CambridgeImport : BaseEntity
{
    public Guid   OwnerId    { get; set; }
    /// <summary>The owner's library key (questions store their author's email).</summary>
    public string OwnerEmail { get; set; } = string.Empty;

    /// <summary>1 = Question Paper read, 2 = Mark Scheme matched, 3 = something saved. Drives "Continue import".</summary>
    public int LastCompletedStep { get; set; }

    // ── What the paper is (detected, then checked by the teacher) ──────────────────────────────────────────────
    public string  SyllabusCode { get; set; } = "9709";
    /// <summary>Component / paper number: 1 = Paper 1 Pure Mathematics 1, … 6.</summary>
    public int?    Component    { get; set; }
    /// <summary>Variant digit ("2" in 9709/12); null for a specimen ("9709/01").</summary>
    public string? Variant      { get; set; }
    /// <summary>"February/March", "May/June", "October/November" or "Specimen".</summary>
    public string? Session      { get; set; }
    public int?    Year         { get; set; }
    /// <summary>True once the teacher has looked at the detected details (or corrected them).</summary>
    public bool    DetailsChecked { get; set; }
    /// <summary>The teacher's own subject text for the saved questions (kept apart from the Cambridge topic).</summary>
    public string? Subject      { get; set; } = "Mathematics";

    /// <summary>The published syllabus version used for topics (the one covering <see cref="Year"/>).</summary>
    public Guid?   SyllabusVersionId { get; set; }

    // ── Question Paper ─────────────────────────────────────────────────────────────────────────────────────────
    public string  PaperFileName { get; set; } = string.Empty;
    public string  PaperSha256   { get; set; } = string.Empty;
    public int     PaperPages    { get; set; }
    public ImportStage PaperStage { get; set; }
    public string? PaperError    { get; set; }
    /// <summary>The model's reading of the paper, kept so nothing is read twice.</summary>
    public string? PaperJson     { get; set; }

    // ── Mark Scheme ────────────────────────────────────────────────────────────────────────────────────────────
    public string? MarkSchemeFileName { get; set; }
    public string? MarkSchemeSha256   { get; set; }
    public int     MarkSchemePages    { get; set; }
    public ImportStage MarkSchemeStage { get; set; }
    public string? MarkSchemeError    { get; set; }
    public string? MarkSchemeJson     { get; set; }
    /// <summary>What the mark scheme says it is (detected from its own text), as JSON.</summary>
    public string? MarkSchemeDetailsJson { get; set; }
    /// <summary>The teacher confirmed a mark scheme whose details could not be read belongs to this paper.</summary>
    public bool    MarkSchemeDetailsConfirmed { get; set; }

    public DateTime? StageStartedAt { get; set; }

    public List<CambridgeImportPart> Parts { get; set; } = new();

    /// <summary>"9709/12", or "9709/01" for a specimen.</summary>
    public string PaperCode => Component is null ? SyllabusCode
        : Variant is null ? $"{SyllabusCode}/0{Component}" : $"{SyllabusCode}/{Component}{Variant}";

    /// <summary>"9709/12 · May/June 2026".</summary>
    public string Label => $"{PaperCode}{(Session is null && Year is null ? "" : " · ")}{Session}{(Year is null ? "" : $" {Year}")}".Trim();
}

/// <summary>
/// One answerable subpart of an imported paper, down to e.g. 3(b)(ii). The question's shared text and figure are
/// copied onto every subpart that uses them, so each saved question is complete on its own.
/// </summary>
public class CambridgeImportPart : BaseEntity
{
    public Guid ImportId { get; set; }
    public CambridgeImport Import { get; set; } = null!;

    public int    QuestionNumber { get; set; }
    /// <summary>Normalised full number, e.g. "3(b)(ii)" — the key for matching the mark scheme.</summary>
    public string Number { get; set; } = string.Empty;
    public int    Order  { get; set; }

    /// <summary>The question's shared text (before its first part), LaTeX in $…$.</summary>
    public string? Stem  { get; set; }
    /// <summary>This subpart's own text, including any text printed between parts that it depends on.</summary>
    public string  Text  { get; set; } = string.Empty;
    public int?    Marks { get; set; }
    public int?    PaperPage { get; set; }
    /// <summary>"ShortAnswer" or "OpenText" (show that / sketch / explain).</summary>
    public string  Kind  { get; set; } = "ShortAnswer";
    /// <summary>The student answers by drawing on the sketch pad (over the question's figure, when it has one).</summary>
    public bool    NeedsDrawing { get; set; }
    /// <summary>Set while the answer format is unclear (the wording and the reading disagree): why, for the teacher
    /// to choose a drawn or a written answer on the review step. Null once clear or chosen.</summary>
    public string? FormatQuestion { get; set; }

    /// <summary>The figure this subpart uses (shared by the question's parts), as a key into the import's figures.</summary>
    public string? FigureKey  { get; set; }
    /// <summary>Where the figure is on the paper: page and box (fractions of the page), as JSON.</summary>
    public string? FigureBox  { get; set; }
    public string? FigureDescription { get; set; }

    // ── Topic ──────────────────────────────────────────────────────────────────────────────────────────────────
    public Guid?   SyllabusTopicId { get; set; }
    public SyllabusTopic? SyllabusTopic { get; set; }
    /// <summary>"suggested" (from the paper reading), "teacher" (chosen or changed) or "none" (choose manually).</summary>
    public string  TopicSource { get; set; } = "none";

    // ── Answer key from the mark scheme ────────────────────────────────────────────────────────────────────────
    public string? AnswerKey  { get; set; }
    /// <summary>The marking criteria as the scheme gives them (M1/A1/B1, dependencies, alternatives, guidance).</summary>
    public string? Criteria   { get; set; }
    public int?    SchemeMarks { get; set; }
    /// <summary>Mark scheme pages the criteria came from, e.g. "7" or "7–8".</summary>
    public string? SchemePages { get; set; }
    /// <summary>True when the teacher edited the key or criteria; a later reading then never overwrites silently.</summary>
    public bool    EditedByTeacher { get; set; }
    /// <summary>A newer reading that differs from the teacher's version, waiting for the teacher's choice (JSON).</summary>
    public string? PendingReplacement { get; set; }

    public ImportPartStatus Status { get; set; }
    /// <summary>Why the part needs review — shown to the teacher as is.</summary>
    public string? ReviewReason { get; set; }

    public Guid?   SavedQuestionId { get; set; }
}
