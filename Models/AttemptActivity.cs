namespace SmartSchoolAI.Models;

/// <summary>
/// One action a student took while working on (or after handing in) one attempt. Deliberately small: which attempt
/// (assignment, student, attempt number), which question when that is known, two one-byte codes and the time. Nothing
/// the student typed, copied or asked is stored — no clipboard text, no keystrokes, no prompts or replies.
/// </summary>
public class AttemptActivity
{
    public long     Id            { get; set; }
    public Guid     AssignmentId  { get; set; }
    public Guid     StudentId     { get; set; }
    public int      AttemptNumber { get; set; }
    /// <summary>0-based question index, or null when the action is about the work as a whole.</summary>
    public short?   QuestionIndex { get; set; }
    /// <summary><see cref="ActivityKind"/></summary>
    public byte     Kind          { get; set; }
    /// <summary><see cref="ActivityOutcome"/></summary>
    public byte     Outcome       { get; set; }
    /// <summary>When the action happened, in UTC: the server's own time for server-side actions; for browser actions the
    /// browser's time corrected by the clock difference measured when its batch arrived.</summary>
    public DateTime OccurredAt    { get; set; }
    /// <summary>When the server received it (UTC).</summary>
    public DateTime ReceivedAt    { get; set; }
    /// <summary>Set by the client for each action (and for each tutor request until its result): a repeated delivery
    /// with the same id is the same action, never counted twice.</summary>
    public Guid?    ClientId      { get; set; }
    /// <summary><see cref="ActivityFlags"/></summary>
    public byte     Flags         { get; set; }
}

/// <summary>Totals kept for an attempt after its detailed actions passed the retention period and were removed.</summary>
public class AttemptActivitySummary
{
    public long     Id             { get; set; }
    public Guid     AssignmentId   { get; set; }
    public Guid     StudentId      { get; set; }
    public int      AttemptNumber  { get; set; }
    public int      Copies         { get; set; }
    public int      Pastes         { get; set; }
    public int      PastesBlocked  { get; set; }
    public int      HintsRequested { get; set; }
    public int      HintsReceived  { get; set; }
    public int      TutorOpened    { get; set; }
    public int      TutorRequests  { get; set; }
    /// <summary>Details recorded before this time were removed.</summary>
    public DateTime PurgedBefore   { get; set; }
}

public static class ActivityKind
{
    public const byte Copy = 1, Cut = 2, Paste = 3, HintRequested = 4, TutorOpened = 5, TutorMessage = 6, TutorRequest = 7;
}

public static class ActivityOutcome
{
    public const byte Allowed = 1, Blocked = 2, Received = 3, LimitReached = 4, Error = 5, Interrupted = 6, Refused = 7, Pending = 8;
}

[Flags]
public enum ActivityFlags : byte
{
    None = 0,
    /// <summary>Reported by the browser (an observation in the page, not a server fact).</summary>
    FromBrowser = 1,
    /// <summary>The browser's clock could not be matched to the server's well enough to order it exactly.</summary>
    OrderUncertain = 2
}
