namespace SmartSchoolAI.Services;

/// <summary>
/// Physical storage for user profile photos.
///
/// Like submission photos, these live outside <c>wwwroot</c> so the static-file
/// middleware can't hand them to anonymous callers — a profile photo is personal
/// data. They are served only through <c>AvatarFilesController</c>, which requires
/// a signed-in user (any authenticated user, since avatars appear across the UI —
/// class rosters, the nav bar, a teacher viewing a student).
///
/// The web path stored on <c>User.AvatarUrl</c> is unchanged (<c>/uploads/avatars/…</c>),
/// so existing rows and views keep working; only the folder the bytes sit in moved.
/// Default avatars point at a Sneat template asset under <c>/sneat/…</c> and stay
/// public — they carry no personal data.
/// </summary>
public static class AvatarStorage
{
    /// <summary>URL prefix stored on users and routed to the authorized controller.</summary>
    public const string WebPrefix = "/uploads/avatars";

    private const string StorageFolder = "App_Data";

    public static string RootFor(IWebHostEnvironment env)
        => Path.Combine(env.ContentRootPath, StorageFolder, "avatars");

    /// <summary>Writes one avatar (named by user id) and returns the web path to record.</summary>
    public static async Task<string> SaveAsync(
        IWebHostEnvironment env, byte[] bytes, string extension, Guid userId)
    {
        var dir = RootFor(env);
        Directory.CreateDirectory(dir);

        var fileName = $"{userId}{extension}";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), bytes);

        return $"{WebPrefix}/{fileName}";
    }

    /// <summary>Deletes the file behind a stored avatar web path, if it is one of ours.</summary>
    public static void DeleteByWebPath(IWebHostEnvironment env, string? avatarUrl)
    {
        if (string.IsNullOrEmpty(avatarUrl) || !avatarUrl.StartsWith(WebPrefix + "/", StringComparison.Ordinal))
            return;

        var path = FindFile(env, Path.GetFileName(avatarUrl));
        if (path is not null)
            File.Delete(path);
    }

    /// <summary>
    /// Maps a requested file name to disk, or null when the name is malformed or the
    /// file is missing. The name comes from the URL, so it is checked against anything
    /// that could point outside the avatars folder.
    /// </summary>
    public static string? FindFile(IWebHostEnvironment env, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            return null;

        var root     = RootFor(env);
        var fullPath = Path.GetFullPath(Path.Combine(root, fileName));

        if (!fullPath.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                                 StringComparison.OrdinalIgnoreCase))
            return null;

        return File.Exists(fullPath) ? fullPath : null;
    }

    public static string ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".webp" => "image/webp",
            ".gif"  => "image/gif",
            ".bmp"  => "image/bmp",
            _       => "image/jpeg"
        };

    /// <summary>
    /// One-time move of avatars written before this folder existed, when they were
    /// served publicly from under the web root. Runs on every start and does nothing
    /// once the old folder is gone.
    /// </summary>
    public static void MigratePubliclyStoredFiles(IWebHostEnvironment env)
    {
        if (string.IsNullOrEmpty(env.WebRootPath))
            return;

        var legacyRoot = Path.Combine(env.WebRootPath, "uploads", "avatars");
        if (!Directory.Exists(legacyRoot))
            return;

        var targetRoot = RootFor(env);
        Directory.CreateDirectory(targetRoot);

        foreach (var legacyFile in Directory.EnumerateFiles(legacyRoot))
        {
            var target = Path.Combine(targetRoot, Path.GetFileName(legacyFile));
            if (File.Exists(target))
                File.Delete(legacyFile);   // an earlier run already moved it
            else
                File.Move(legacyFile, target);
        }

        // Leaving the folder behind would keep serving anything that lands in it later.
        Directory.Delete(legacyRoot, recursive: true);
    }
}
