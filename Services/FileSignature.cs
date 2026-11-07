namespace SmartSchoolAI.Services;

/// <summary>
/// Checks a file's first bytes against known magic numbers, so uploads are
/// validated by what they actually are rather than trusting the client-supplied
/// filename extension or Content-Type — both of which are trivial to fake.
/// </summary>
public static class FileSignature
{
    public static bool IsPdf(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 5 &&
        bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D; // "%PDF-"

    public static bool IsSupportedImage(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4) return false;

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return true;

        // PNG: 89 50 4E 47
        if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return true;

        // GIF: "GIF87a" / "GIF89a"
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return true;

        // BMP: "BM"
        if (bytes[0] == 0x42 && bytes[1] == 0x4D) return true;

        // WEBP: "RIFF"...."WEBP"
        if (bytes.Length >= 12 &&
            bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
            bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return true;

        // HEIC/HEIF: ISO base media file, "ftyp" box starting at offset 4
        // (brand itself — heic/heix/mif1/msf1/... — varies, so this checks the
        // container format rather than every possible brand fourCC).
        if (bytes.Length >= 12 && bytes[4] == 0x66 && bytes[5] == 0x74 && bytes[6] == 0x79 && bytes[7] == 0x70) return true;

        return false;
    }
}
