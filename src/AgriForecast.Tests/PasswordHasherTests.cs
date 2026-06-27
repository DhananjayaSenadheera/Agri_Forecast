using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Security;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for PasswordHasher (PBKDF2 via ASP.NET Core Identity PasswordHasher&lt;User&gt;).
/// These are pure unit tests — no DI, no DB.
///
/// BUG DOCUMENTED: Verify_GarbageHash_ThrowsFormatException confirms that the
/// production PasswordHasher.Verify() propagates FormatException when given a
/// non-base64 string, instead of returning false. The fix is to catch the exception
/// in PasswordHasher.Verify() and return false.
/// See: AgriForecast.Infrastructure/Security/PasswordHasher.cs line 23 (Verify method).
/// </summary>
public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    // ──────────────────────────────────────────────────────────────────────────────
    // 1. Round-trip: Hash then Verify returns true
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hash_ThenVerify_SamePassword_ReturnsTrue()
    {
        const string password = "S3cur3P@ssword!";
        var hash = _hasher.Hash(password);

        _hasher.Verify(hash, password).Should().BeTrue();
    }

    [Fact]
    public void Hash_ThenVerify_DifferentPassword_ReturnsFalse()
    {
        const string password = "CorrectPassword123";
        var hash = _hasher.Hash(password);

        _hasher.Verify(hash, "WrongPassword456").Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 2. Plaintext must never equal its hash
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hash_Result_IsNotPlaintext()
    {
        const string password = "PlaIntextPass1";
        var hash = _hasher.Hash(password);

        hash.Should().NotBe(password, "PBKDF2 hash must differ from the plaintext");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 3. Same password produces different salts (hashes are not identical)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Hash_SamePassword_TwiceDifferentHash()
    {
        const string password = "SamePass!99";
        var hash1 = _hasher.Hash(password);
        var hash2 = _hasher.Hash(password);

        hash1.Should().NotBe(hash2, "each call embeds a unique random salt");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 4. A malformed / non-base64 stored hash must be treated as a MISMATCH (false),
    //    never as an unhandled exception. (Regression test for a fixed bug: Verify()
    //    previously let FormatException from the Identity hasher escape.)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_GarbageHash_ReturnsFalse()
    {
        _hasher.Verify("not-a-real-hash", "anypassword").Should().BeFalse(
            "a corrupted/garbage stored hash is a mismatch, not a crash");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 5. Edge-case passwords
    // ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("12345678")]              // minimum 8 chars
    [InlineData("P@ssw0rd with spaces")]
    [InlineData("Unicode密碼123")]
    public void Hash_EdgeCasePasswords_VerifiesCorrectly(string password)
    {
        var hash = _hasher.Hash(password);
        _hasher.Verify(hash, password).Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 6. Empty password against a real hash returns false (not a throw)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Verify_EmptyStringPassword_AgainstRealHash_ReturnsFalse()
    {
        var hash = _hasher.Hash("realPassword1");
        _hasher.Verify(hash, string.Empty).Should().BeFalse();
    }
}
