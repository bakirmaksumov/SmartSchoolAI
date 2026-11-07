using System.Security.Cryptography;
using System.Text;
using BCryptNet = BCrypt.Net.BCrypt;

namespace SmartSchoolAI.Services;

/// <summary>
/// Hashes and verifies passwords with BCrypt: every password gets its own random
/// salt, and the work factor makes brute-forcing a stolen database expensive.
///
/// Accounts created before this change hold a legacy SHA-256 hash built on one
/// salt shared by every user. Those still verify, so no one is locked out, and
/// <see cref="NeedsUpgrade"/> reports them so the caller can re-hash the password
/// the next time its owner signs in — see <see cref="AuthService.ValidateAsync"/>.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Cost of one hash. Each step up doubles the work; 12 is the current common baseline.</summary>
    private const int WorkFactor = 12;

    /// <summary>Salt shared by all pre-BCrypt hashes. Kept only to verify old passwords once.</summary>
    private const string LegacySalt = "SmartSchool_Salt_2026_";

    public static string Hash(string password)
        => BCryptNet.HashPassword(password, WorkFactor);

    public static bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;

        if (NeedsUpgrade(hash))
            return VerifyLegacy(password, hash);

        // A hash damaged in storage must fail the check, not crash the sign-in.
        try   { return BCryptNet.Verify(password, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }

    /// <summary>True when the stored hash predates BCrypt and should be replaced.</summary>
    public static bool NeedsUpgrade(string hash)
        => !hash.StartsWith("$2", StringComparison.Ordinal);

    private static bool VerifyLegacy(string password, string hash)
    {
        var computed = SHA256.HashData(Encoding.UTF8.GetBytes(LegacySalt + password));

        byte[] stored;
        try   { stored = Convert.FromBase64String(hash); }
        catch (FormatException) { return false; }

        // Compare in fixed time so a wrong guess reveals nothing through timing.
        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
