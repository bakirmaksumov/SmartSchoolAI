using System.Security.Cryptography;
using System.Text;
using SmartSchoolAI.Services;

namespace SmartSchoolAI.Tests.Services;

public class PasswordHasherTests
{
    [Fact]
    public void Hash_ProducesBCryptHash()
    {
        var hash = PasswordHasher.Hash("correct-horse-battery-staple");

        Assert.StartsWith("$2", hash);
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        // Each call must use its own random salt, otherwise identical passwords
        // would be recognisable from the stored hash alone.
        var hash1 = PasswordHasher.Hash("same-password");
        var hash2 = PasswordHasher.Hash("same-password");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        var hash = PasswordHasher.Hash("my-password");

        Assert.True(PasswordHasher.Verify("my-password", hash));
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("my-password");

        Assert.False(PasswordHasher.Verify("not-my-password", hash));
    }

    [Fact]
    public void Verify_EmptyStoredHash_ReturnsFalse()
    {
        Assert.False(PasswordHasher.Verify("anything", ""));
    }

    [Fact]
    public void Verify_CorruptedBCryptHash_ReturnsFalseInsteadOfThrowing()
    {
        Assert.False(PasswordHasher.Verify("anything", "$2a$not-a-real-hash"));
    }

    [Fact]
    public void NeedsUpgrade_BCryptHash_ReturnsFalse()
    {
        var hash = PasswordHasher.Hash("password");

        Assert.False(PasswordHasher.NeedsUpgrade(hash));
    }

    [Fact]
    public void NeedsUpgrade_LegacyShaHash_ReturnsTrue()
    {
        var legacyHash = LegacyHash("password");

        Assert.True(PasswordHasher.NeedsUpgrade(legacyHash));
    }

    [Fact]
    public void Verify_LegacyHashWithCorrectPassword_ReturnsTrue()
    {
        var legacyHash = LegacyHash("old-password");

        Assert.True(PasswordHasher.Verify("old-password", legacyHash));
    }

    [Fact]
    public void Verify_LegacyHashWithWrongPassword_ReturnsFalse()
    {
        var legacyHash = LegacyHash("old-password");

        Assert.False(PasswordHasher.Verify("wrong-password", legacyHash));
    }

    /// <summary>Reproduces the legacy SHA-256 scheme PasswordHasher must still verify.</summary>
    private static string LegacyHash(string password)
    {
        const string legacySalt = "SmartSchool_Salt_2026_";
        var computed = SHA256.HashData(Encoding.UTF8.GetBytes(legacySalt + password));
        return Convert.ToBase64String(computed);
    }
}
