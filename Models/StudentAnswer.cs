namespace SmartSchoolAI.Models;

/// <summary>
/// Stores student's typed answer for each individual question in an assignment attempt.
/// Linked to a StudentResult (one StudentResult has many StudentAnswers).
/// </summary>
public class StudentAnswer : BaseEntity
{
    public int      QuestionIndex { get; set; }                 // 0-based index in Assignment.Questions JSON
    public string   QuestionId    { get; set; } = string.Empty; // matches Question.Id from JSON
    public string   AnswerValue   { get; set; } = string.Empty; // JSON: typed answer, selected option, etc.
    public bool?    IsCorrect     { get; set; }                 // null = not yet evaluated
    public decimal? PointsAwarded { get; set; }                 // supports partial credit (e.g. 0.5)
    public string?  ImagePath     { get; set; }                 // handwritten photo for this question (null = typed answer)
    public string?  AiSummary     { get; set; }                 // AI feedback for this question (handwritten flow)

    /// <summary>True when a teacher manually adjusted the AI's grade for this answer.</summary>
    public bool     TeacherOverridden { get; set; }

    /// <summary>Optional teacher feedback for this question, shown to the student.</summary>
    public string?  TeacherComment    { get; set; }

    /// <summary>
    /// The student's working ("show your work") typed alongside the answer.
    /// Not auto-graded — it exists so the teacher can see how the answer was reached.
    /// </summary>
    public string?  WorkText          { get; set; }

    /// <summary>
    /// Set when the AI's grade could not be confirmed — the verification pass
    /// disagreed or was unsure. The teacher is asked to confirm this one by hand.
    /// </summary>
    public bool     NeedsReview       { get; set; }

    /// <summary>Why the grade was flagged, written for the teacher.</summary>
    public string?  ReviewNote        { get; set; }

    /// <summary>Verifier's confidence in the grade, 0.0–1.0. Null when not verified.</summary>
    public double?  AiConfidence      { get; set; }

    // FK
    public Guid StudentResultId { get; set; }

    // Navigation
    public StudentResult StudentResult { get; set; } = null!;
}
