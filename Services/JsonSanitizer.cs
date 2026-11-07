namespace SmartSchoolAI.Services;

/// <summary>
/// Repairs JSON produced by language models before deserialization.
/// Models frequently emit LaTeX (e.g. \sqrt, \frac, \cdot, \times) with single
/// backslashes, which are invalid JSON escapes and would throw.
/// In this LaTeX-heavy domain a backslash before a letter is usually a command,
/// not a control char (\f is \frac, \t is \times, \n is \neq …), so only \" \\ \/
/// and \uXXXX are treated as real escapes by default and every other backslash
/// is doubled so it survives deserialization as a literal LaTeX command.
///
/// The one exception is \n \t \r OUTSIDE a $...$ math span: models legitimately
/// use those as real JSON escapes to put line breaks/tabs into plain question
/// text (e.g. "...is a constant.\nShowing all working..."). Doubling those turned
/// intentional line breaks into a literal "\n" in the rendered question. Inside
/// $...$ they still mean the LaTeX command (\neq, \tan, \rightarrow, …), so the
/// exception only applies outside math spans.
///
/// `inMath` is reset at every JSON string boundary (each closing/opening `"`),
/// not just left running for the whole document. A math span cannot legitimately
/// cross a string boundary — it lives inside one JSON string value — so without
/// this reset, one stray/unpaired `$` anywhere in a multi-question AI response
/// (a malformed LaTeX span, a literal currency `$`) permanently flips the
/// parity for every question that follows in the same response, silently
/// turning their real `\n` line breaks into a literal two-character "\n" in
/// the stored text. Scoping the toggle to one string at a time contains a bad
/// `$` to the one field it appeared in, instead of corrupting the rest of the
/// batch.
/// </summary>
public static class JsonSanitizer
{
    public static string FixInvalidBackslashes(string json)
    {
        if (string.IsNullOrEmpty(json)) return json;

        var sb = new System.Text.StringBuilder(json.Length + 16);
        bool inMath = false;
        int i = 0;
        while (i < json.Length)
        {
            char c = json[i];

            // A bare, unescaped '"' is always a JSON string boundary here: an
            // escaped quote (\") is consumed as a pair by the '\\' branch below
            // and never reaches this check as a standalone character.
            if (c == '"')
            {
                inMath = false;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '$')
            {
                inMath = !inMath;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '\\')
            {
                char next = i + 1 < json.Length ? json[i + 1] : '\0';

                if (next is '"' or '\\' or '/')
                {
                    sb.Append(c).Append(next);
                    i += 2;
                    continue;
                }

                if (next == 'u' && i + 5 < json.Length
                    && Uri.IsHexDigit(json[i + 2]) && Uri.IsHexDigit(json[i + 3])
                    && Uri.IsHexDigit(json[i + 4]) && Uri.IsHexDigit(json[i + 5]))
                {
                    sb.Append(json, i, 6);
                    i += 6;
                    continue;
                }

                if (!inMath && next is 'n' or 't' or 'r')
                {
                    sb.Append(c).Append(next);
                    i += 2;
                    continue;
                }

                // Stray backslash (LaTeX command) -> double it so it survives as a literal.
                sb.Append('\\').Append('\\');
                i++;
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
