namespace SmartSchoolAI.Services;

/// <summary>
/// A minimal, bounded-size trail of who did what: sign-ins, an assignment or
/// question being created, a student joining a class, a question being answered.
/// One line per event — a timestamp, the action, the actor's role and id, and a
/// couple of ids as context. Deliberately not answer text, question content, names,
/// emails, or anything else a reader wouldn't need to know an action happened.
///
/// This is intentionally not routed through <see cref="ILogger"/>: the framework's
/// own request/EF/routing logging is verbose by design and would swamp a small
/// audit trail sharing its sink. A dedicated file, capped and rotated on write,
/// keeps this file's growth predictable regardless of how chatty other logging is.
///
/// Named distinctly from <see cref="Models.AuditLog"/>, an unrelated (and
/// currently unused) database entity for GDPR-style before/after change tracking
/// — heavier than this by design (JSON snapshots, IP, user agent) and a separate
/// concern from this lightweight activity trail.
/// </summary>
public static class ActivityLog
{
    private const long MaxBytes   = 1 * 1024 * 1024;   // ~1 MB — several thousand lines
    private const string FileName = "activity.log";

    // File.AppendAllText is not safe for concurrent writers; a request-handling app
    // has many. The lock only guards the (rotate-check + append) pair, which is fast.
    private static readonly object Gate = new();

    public static void Record(IWebHostEnvironment env, string action, string actorRole, Guid actorId, string detail = "")
    {
        var line =
            $"{DateTime.UtcNow:yyyy-MM-dd'T'HH:mm:ss'Z'}\t{action}\t{actorRole}\t{actorId}" +
            (string.IsNullOrEmpty(detail) ? "" : $"\t{detail}");

        var path = LogPath(env);

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                RotateIfOversized(path);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // A locked or briefly inaccessible file loses one audit line, not the
            // request it was recording — the action that triggered it must still succeed.
        }
    }

    private static string LogPath(IWebHostEnvironment env) =>
        Path.Combine(env.ContentRootPath, "App_Data", "logs", FileName);

    /// <summary>
    /// Keeps at most two generations on disk (the live file plus one backup), so
    /// total footprint never exceeds roughly 2x <see cref="MaxBytes"/>. A backup is
    /// overwritten wholesale on each rotation — this is a bounded activity trail,
    /// not an archive, so older history is expected to age out.
    /// </summary>
    private static void RotateIfOversized(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaxBytes) return;

        var backup = path + ".1";
        if (File.Exists(backup)) File.Delete(backup);
        File.Move(path, backup);
    }
}
