namespace SmartSchoolAI.Models.Dtos;

/// <summary>
/// A single transcribed step of a student's handwritten solution.
/// During OCR only <see cref="Number"/> and <see cref="Text"/> are filled;
/// <see cref="Status"/> and <see cref="Explanation"/> are added during evaluation.
/// </summary>
public class HandwritingStepDto
{
    public int    Number      { get; set; }
    public string Text        { get; set; } = string.Empty;
    public string Status      { get; set; } = string.Empty;   // "correct" | "incorrect"
    public string Explanation { get; set; } = string.Empty;
}

/// <summary>
/// Result returned by <see cref="Services.HandwritingAnalysisService"/>.
/// Used for both the OCR-only pass (steps without status) and the
/// evaluation pass (steps with status + overall verdict).
/// </summary>
public class HandwritingAnalysisDto
{
    public string                  DetectedProblem { get; set; } = string.Empty;
    public string                  ExpectedAnswer  { get; set; } = string.Empty;
    public List<HandwritingStepDto> Steps          { get; set; } = new();
    public string                  Overall         { get; set; } = string.Empty;   // "correct" | "incorrect"
    public string                  Summary         { get; set; } = string.Empty;
}

/// <summary>
/// Second-opinion check on a grade produced by the evaluation pass.
/// A single grading call can mark a correct solution wrong (e.g. treating a
/// boundary case as strict), so an independent, deliberately sceptical pass
/// re-solves the task and reports whether it agrees and how sure it is.
/// </summary>
public class GradingVerificationDto
{
    /// <summary>True when the verifier reaches the same verdict as the grader.</summary>
    public bool   Agrees     { get; set; }

    /// <summary>Verifier's own verdict: "correct" | "incorrect".</summary>
    public string Overall    { get; set; } = string.Empty;

    /// <summary>How sure the verifier is, 0.0–1.0.</summary>
    public double Confidence { get; set; }

    /// <summary>Short explanation for the teacher when something looks off.</summary>
    public string Notes      { get; set; } = string.Empty;
}
