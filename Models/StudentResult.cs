namespace SmartSchoolAI.Models;

public enum ResultStatus
{
    Correct   = 0,
    Incorrect = 1,
    Partial   = 2
}

/// <summary>
/// Stores the AI-evaluated result of a student's assignment submission.
/// One record per student per assignment per attempt.
/// </summary>
public class StudentResult : BaseEntity
{
    public string      ImagePath     { get; set; } = string.Empty;
    public ResultStatus OverallStatus { get; set; }
    public int         Score         { get; set; }         // 0-100 (may exceed 100 with bonus)
    public int         TotalSteps    { get; set; }
    public string?     AiSummary     { get; set; }
    public DateTime    SubmittedAt   { get; set; } = DateTime.UtcNow;
    public int         AttemptNumber { get; set; } = 1;

    /// <summary>
    /// False while the student is still working (answers saved incrementally,
    /// e.g. per-question photo solutions). Only a submitted result counts as a
    /// used attempt. Set true when the student finally submits the quiz.
    /// </summary>
    public bool        IsSubmitted   { get; set; }

    /// <summary>Teacher-awarded extra points added on top of per-question points.</summary>
    public decimal     BonusPoints   { get; set; }

    /// <summary>Set when a teacher has reviewed/graded this submission. Null = AI-only.</summary>
    public DateTime?   TeacherReviewedAt { get; set; }

    // FK
    public Guid AssignmentId { get; set; }
    public Guid StudentId    { get; set; }

    // Navigation
    public Assignment              Assignment   { get; set; } = null!;
    public User                    Student      { get; set; } = null!;
    public ICollection<StepResult>     StepResults    { get; set; } = new List<StepResult>();
    public ICollection<ChatSession>    ChatSessions   { get; set; } = new List<ChatSession>();
    public ICollection<StudentAnswer>  StudentAnswers { get; set; } = new List<StudentAnswer>();

    /// <summary>
    /// Recomputes <see cref="Score"/> and <see cref="OverallStatus"/> from the
    /// per-question points awarded plus any teacher <see cref="BonusPoints"/>.
    /// Each question is worth 1 point. Score may exceed 100 when bonus is given.
    /// </summary>
    /// <param name="passThreshold">
    /// The assignment's pass mark. At or above it the work counts as correct;
    /// at or above half of it as partial; below that as incorrect.
    /// </param>
    public void RecomputeScore(int totalQuestions, int passThreshold)
    {
        var awarded = StudentAnswers.Sum(a => a.PointsAwarded ?? 0m);

        // No upper clamp — bonus is meant to be able to push a score past 100.
        // A lower clamp is still needed: nothing legitimately produces a
        // negative percentage, but an unclamped BonusPoints override could.
        Score = totalQuestions > 0
            ? Math.Max(0, (int)Math.Round((awarded + BonusPoints) * 100m / totalQuestions, MidpointRounding.AwayFromZero))
            : 0;

        TotalSteps    = totalQuestions;
        OverallStatus = Score >= passThreshold     ? ResultStatus.Correct
                      : Score >= passThreshold / 2 ? ResultStatus.Partial
                      :                              ResultStatus.Incorrect;
        UpdatedAt     = DateTime.UtcNow;
    }
}
