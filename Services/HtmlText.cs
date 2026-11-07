using System.Net;
using System.Text.RegularExpressions;

namespace SmartSchoolAI.Services;

/// <summary>
/// Small text helpers for cleaning rich-editor input before persistence.
/// Question titles are displayed HTML-encoded, so any markup pasted into the
/// editor (font/color/span) would show up as literal text — we strip it here.
/// </summary>
public static partial class HtmlText
{
    // Match only real HTML tags (`<` + optional `/` + a letter), so mathematical
    // inequalities like "x < -3a" or "|x+3a| > a" are left intact.
    [GeneratedRegex("</?[a-zA-Z][^>]*>")]
    private static partial Regex TagRegex();

    /// <summary>Removes HTML tags and decodes entities, returning clean plain text.</summary>
    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Decode BEFORE stripping tags, not after: an entity-encoded tag
        // ("&lt;img src=x onerror=..&gt;") has no literal '<'/'>' for TagRegex
        // to match, so stripping-then-decoding let it survive untouched and
        // only turn into a real, executable tag once something downstream
        // (this method's own old decode step, or a client-side reload into
        // innerHTML) decoded it later. Decoding first means any tag hiding
        // behind entities is normalised to real angle brackets and removed by
        // the same pass that catches literal ones.
        var decoded = WebUtility.HtmlDecode(html);
        return TagRegex().Replace(decoded, string.Empty).Trim();
    }
}
