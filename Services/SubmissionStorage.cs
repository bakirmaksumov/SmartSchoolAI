namespace SmartSchoolAI.Services;

/// <summary>
/// Physical storage for the photos students upload with their solutions.
///
/// The files live outside <c>wwwroot</c> on purpose. Anything under the web root is
/// handed out by the static-file middleware to whoever knows the URL, and a photo of
/// a student's work is their personal data — it must only be reachable through
/// <c>SubmissionFilesController</c>, which checks who is asking first.
///
/// The web path kept in the database is deliberately unchanged, so answers saved
/// before this move still resolve; only the folder the bytes sit in has changed.
/// </summary>
public static class SubmissionStorage
{
    /// <summary>URL prefix stored on answers and routed to the authorized controller.</summary>
    public const string WebPrefix = "/uploads/submissions";

    private const string StorageFolder = "App_Data";

    /// <summary>Folder holding every student's submission photos.</summary>
    public static string RootFor(IWebHostEnvironment env)
        => Path.Combine(env.ContentRootPath, StorageFolder, "submissions");

    /// <summary>Writes one photo and returns the web path to record on the answer.</summary>
    public static async Task<string> SaveAsync(
        IWebHostEnvironment env, byte[] bytes, string extension, Guid studentId)
    {
        var dir = Path.Combine(RootFor(env), studentId.ToString());
        Directory.CreateDirectory(dir);

        var fileName = $"{Guid.NewGuid()}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes);

        return $"{WebPrefix}/{studentId}/{fileName}";
    }

    /// <summary>
    /// Maps a requested file back to disk, or null when the name is malformed or the
    /// file is missing. The name comes from the URL, so it is checked against anything
    /// that could point outside the student's own folder.
    /// </summary>
    public static string? FindFile(IWebHostEnvironment env, Guid studentId, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            return null;

        var studentDir = Path.Combine(RootFor(env), studentId.ToString());
        var fullPath   = Path.GetFullPath(Path.Combine(studentDir, fileName));

        // Belt and braces: the resolved path must still sit inside the student's folder.
        if (!fullPath.StartsWith(Path.GetFullPath(studentDir) + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    public static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png"             => "image/png",
            ".webp"            => "image/webp",
            ".heic" or ".heif" => "image/heic",
            ".bmp"             => "image/bmp",
            ".gif"             => "image/gif",
            _                  => "image/jpeg"
        };

    /// <summary>
    /// One-time move of photos written before this folder existed, when they were
    /// still served publicly from under the web root. Runs on every start and does
    /// nothing once the old folder is gone.
    /// </summary>
    public static void MigratePubliclyStoredFiles(IWebHostEnvironment env)
    {
        if (string.IsNullOrEmpty(env.WebRootPath))
            return;

        var legacyRoot = Path.Combine(env.WebRootPath, "uploads", "submissions");
        if (!Directory.Exists(legacyRoot))
            return;

        var targetRoot = RootFor(env);
        Directory.CreateDirectory(targetRoot);

        foreach (var legacyFile in Directory.EnumerateFiles(legacyRoot, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(targetRoot, Path.GetRelativePath(legacyRoot, legacyFile));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // A copy moved by an earlier run wins; the public leftover is discarded.
            if (File.Exists(target))
                File.Delete(legacyFile);
            else
                File.Move(legacyFile, target);
        }

        // Leaving the folder behind would keep serving anything that lands in it later.
        Directory.Delete(legacyRoot, recursive: true);
    }
}
