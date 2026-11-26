namespace SmartSchoolAI.Services;

/// <summary>
/// Private files of the Content Library and of Cambridge paper imports, kept outside <c>wwwroot</c> (like
/// <see cref="QuestionAttachmentStorage"/>): they reach the browser only through controllers that check who asks.
///   App_Data/cambridge-imports/{importId}/paper.pdf | markscheme.pdf | figures/q{n}.png
///   App_Data/library-files/{questionId}/figure.png  (web path /ContentLibrary/Figure/{questionId})
/// </summary>
public static class LibraryFileStorage
{
    public const string LibraryFigurePrefix = "/ContentLibrary/Figure/";

    private static string Root(IWebHostEnvironment env, string folder) => Path.Combine(env.ContentRootPath, "App_Data", folder);

    public static string ImportFolder(IWebHostEnvironment env, Guid importId) => Path.Combine(Root(env, "cambridge-imports"), importId.ToString());

    /// <summary>"paper" or "markscheme".</summary>
    public static string ImportPdfPath(IWebHostEnvironment env, Guid importId, string which) =>
        Path.Combine(ImportFolder(env, importId), which == "markscheme" ? "markscheme.pdf" : "paper.pdf");

    public static async Task SaveImportPdfAsync(IWebHostEnvironment env, Guid importId, string which, byte[] bytes)
    {
        Directory.CreateDirectory(ImportFolder(env, importId));
        await File.WriteAllBytesAsync(ImportPdfPath(env, importId, which), bytes);
    }

    /// <summary>Figure keys are "q" + a question number; anything else is refused.</summary>
    public static string? FigurePath(IWebHostEnvironment env, Guid importId, string figureKey)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(figureKey ?? "", @"^q\d{1,3}$")) return null;
        return Path.Combine(ImportFolder(env, importId), "figures", figureKey + ".png");
    }

    public static async Task<bool> SaveFigureAsync(IWebHostEnvironment env, Guid importId, string figureKey, byte[] png)
    {
        var path = FigurePath(env, importId, figureKey);
        if (path is null) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, png);
        return true;
    }

    public static string LibraryFigureFile(IWebHostEnvironment env, Guid questionId) =>
        Path.Combine(Root(env, "library-files"), questionId.ToString(), "figure.png");

    /// <summary>Copies an import's figure onto a library question; returns the question's web path, or null.</summary>
    public static string? CopyFigureToLibrary(IWebHostEnvironment env, Guid importId, string? figureKey, Guid questionId)
    {
        if (figureKey is null) return null;
        var source = FigurePath(env, importId, figureKey);
        if (source is null || !File.Exists(source)) return null;
        var target = LibraryFigureFile(env, questionId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(source, target, overwrite: true);
        return LibraryFigurePrefix + questionId;
    }

    /// <summary>The bytes of a library question's figure, if its stored path is one of ours and the file exists.</summary>
    public static byte[]? ReadLibraryFigure(IWebHostEnvironment env, string? webPath)
    {
        if (webPath is null || !webPath.StartsWith(LibraryFigurePrefix, StringComparison.Ordinal)) return null;
        if (!Guid.TryParse(webPath[LibraryFigurePrefix.Length..], out var id)) return null;
        var file = LibraryFigureFile(env, id);
        return File.Exists(file) ? File.ReadAllBytes(file) : null;
    }

    public static void DeleteImport(IWebHostEnvironment env, Guid importId)
    {
        try { var d = ImportFolder(env, importId); if (Directory.Exists(d)) Directory.Delete(d, true); }
        catch { /* best effort */ }
    }
}
