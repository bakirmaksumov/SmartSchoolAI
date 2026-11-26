namespace SmartSchoolAI.Tests.TestHelpers;

/// <summary>A minimal text-only PDF, one string per page (lines split on '\n'), written by hand: iText needs an extra
/// package to write PDFs, and the app only ever reads them. Used for generated TEST DATA documents.</summary>
public static class TestPdf
{
    public static byte[] Build(params string[] pages)
    {
        static string Esc(string t) => t.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)")
            .Replace("•", "-").Replace("—", "-").Replace("–", "-");
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "", "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>" };
        var kids = new List<int>();
        foreach (var page in pages)
        {
            var content = new System.Text.StringBuilder("BT /F1 10 Tf 14 TL 40 800 Td\n");
            foreach (var line in page.Split('\n')) content.Append('(').Append(Esc(line)).Append(") Tj T*\n");
            content.Append("ET");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {objects.Count} 0 R >>");
            kids.Add(objects.Count);
        }
        objects[1] = $"<< /Type /Pages /Count {kids.Count} /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] >>";

        var sb = new System.Text.StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
