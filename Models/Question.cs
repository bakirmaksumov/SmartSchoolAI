namespace SmartSchoolAI.Models;

/// <summary>
/// Represents the type of question used in an assignment or library.
/// </summary>
public enum QuestionType
{
    ShortAnswer         = 0,
    MultipleChoice      = 1,
    OpenText            = 2,
    MathExpression      = 3,
    SingleChoice        = 4,
    Graphing            = 5,
    GeometryDrawing     = 6,
    NumberLine          = 7,
    DotPlot             = 8,
    TableCheck          = 9,
    TableText           = 10,
    VerticalOperation   = 11,
    Protractor          = 12,
    FractionModel       = 13
}

/// <summary>
/// Cognitive difficulty of a question aligned to Bloom's taxonomy tiers.
/// </summary>
public enum DifficultyLevel
{
    Easy   = 1,
    Medium = 2,
    Hard   = 3
}

/// <summary>
/// A reusable question stored in the teacher's content library.
/// Options and SkillIds are stored as JSON arrays for schema simplicity.
/// Soft-deleted via BaseEntity.DeletedAt.
/// </summary>
public class Question : BaseEntity
{
    public string          Title       { get; set; } = string.Empty;
    public QuestionType    Type        { get; set; } = QuestionType.ShortAnswer;
    public DifficultyLevel Difficulty  { get; set; } = DifficultyLevel.Medium;
    public string?         Subject     { get; set; }
    public int?            GradeLevel  { get; set; }

    /// <summary>The expected correct answer used by the AI evaluator.</summary>
    public string  AnswerKey   { get; set; } = string.Empty;

    /// <summary>Step-by-step explanation shown to students after evaluation.</summary>
    public string? Explanation { get; set; }

    /// <summary>JSON array of option strings — used for MultipleChoice type only.</summary>
    public string Options  { get; set; } = "[]";

    /// <summary>
    /// JSON blob of the interactive answer-panel configuration for visual question
    /// types (Graphing, GeometryDrawing, NumberLine, DotPlot, TableCheck/Text,
    /// VerticalOperation, Protractor, FractionModel). Shape varies per type — the
    /// panel that produced it is the only thing that needs to parse it back.
    /// </summary>
    public string? VisualState { get; set; }

    /// <summary>JSON array of Skill GUIDs linked to this question.</summary>
    public string SkillIds { get; set; } = "[]";

    /// <summary>When true, the question is visible to all teachers in the school.</summary>
    public bool IsPublic { get; set; } = false;

    /// <summary>Email of the teacher who created this question (denormalised for simplicity).</summary>
    public string CreatedByEmail { get; set; } = string.Empty;
}
