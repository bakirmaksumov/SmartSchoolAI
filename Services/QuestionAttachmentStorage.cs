namespace SmartSchoolAI.Services;

/// <summary>
/// Physical storage for images attached to assignment questions — either a diagram
/// the teacher attaches (a PDF import never carries figures, only text/LaTeX) or a
/// reference solution image used as an answer key.
///
/// Files live outside <c>wwwroot</c>, same reasoning as <see cref="SubmissionStorage"/>:
/// anything under the web root is handed out by the static-file middleware to whoever
/// knows the URL, and a question can belong to a private assignment — so access must
/// go through <c>QuestionFilesController</c>, which checks who is asking first.
/// </summary>
public static class QuestionAttachmentStorage
{
    /// <summary>URL prefix stored on questions and routed to the authorized controller.</summary>
    public const string WebPrefix = "/uploads/question-files";

    private const string StorageFolder = "App_Data";

    /// <summary>Folder holding every assignment's question attachments.</summary>
    public static string RootFor(IWebHostEnvironment env)
        => Path.Combine(env.ContentRootPath, StorageFolder, "question-files");

    /// <summary>Writes one image and returns the web path to record on the question.</summary>
    public static async Task<string> SaveAsync(
        IWebHostEnvironment env, byte[] bytes, string extension,
        Guid assignmentId, string questionId, string kind)
    {
        var dir = Path.Combine(RootFor(env), assignmentId.ToString(), SafeSegment(questionId));
        Directory.CreateDirectory(dir);

        var fileName = $"{kind}-{Guid.NewGuid()}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes);

        return $"{WebPrefix}/{assignmentId}/{SafeSegment(questionId)}/{fileName}";
    }

    /// <summary>
    /// Maps a stored web path (as recorded on a question) back to a physical path, or
    /// null when the path is malformed, points outside the expected folder, or the
    /// file is missing.
    /// </summary>
    public static string? ResolveFromWebPath(IWebHostEnvironment env, string? webPath)
    {
        if (string.IsNullOrWhiteSpace(webPath) || !webPath.StartsWith(WebPrefix + "/", StringComparison.Ordinal))
            return null;

        var segments = webPath[(WebPrefix.Length + 1)..].Split('/');
        if (segments.Length != 3) return null;

        var (assignmentIdRaw, questionId, fileName) = (segments[0], segments[1], segments[2]);
        if (!Guid.TryParse(assignmentIdRaw, out var assignmentId)) return null;
        if (Path.GetFileName(fileName) != fileName) return null;
        if (SafeSegment(questionId) != questionId) return null;

        var dir      = Path.Combine(RootFor(env), assignmentId.ToString(), questionId);
        var fullPath = Path.GetFullPath(Path.Combine(dir, fileName));

        // Belt and braces: the resolved path must still sit inside the question's folder.
        if (!fullPath.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    /// <summary>Best-effort delete of everything attached to one question — never throws.</summary>
    public static void DeleteQuestionFolder(IWebHostEnvironment env, Guid assignmentId, string questionId)
    {
        try
        {
            var dir = Path.Combine(RootFor(env), assignmentId.ToString(), SafeSegment(questionId));
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup — an orphaned folder is harmless.
        }
    }

    /// <summary>Question ids are teacher/client-generated GUID strings — reject anything else.</summary>
    private static string SafeSegment(string value)
        => Guid.TryParse(value, out var g) ? g.ToString() : "";
}
