using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for JwtTokenGenerator: issued tokens parse, carry the right claims, and are signed with the
/// configured key, issuer and audience.
/// Note: JwtSecurityTokenHandler.ValidateToken() maps short JWT claim names to the ClaimTypes URIs (sub ->
/// NameIdentifier, email -> Email), so these tests use ReadJwtToken() on the raw token to examine claims
/// before name-mapping — which is what the .NET code actually emits.
/// </summary>
public class JwtTokenGeneratorTests
{
    // A test-only JwtSettings. In production the Key must come from a secret store.
    private static JwtSettings TestSettings() => new()
    {
        Key = "TestSuperSecretKey_MustBe16BytesMinimumOrMore!",
        Issuer = "AgriForecast.Tests",
        Audience = "AgriForecast.TestClient",
        AccessTokenMinutes = 60
    };

    private static JwtTokenGenerator BuildGenerator(JwtSettings? settings = null)
    {
        var opts = Options.Create(settings ?? TestSettings());
        return new JwtTokenGenerator(opts);
    }

    private static User TestUser() => new()
    {
        Id = Guid.NewGuid(),
        Username = "farmer_test",
        Email = "farmer@test.lk",
        Role = "Farmer",
        PasswordHash = "irrelevant",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    // Parses the token without validation (to inspect claims as-emitted).
    private static JwtSecurityToken ParseToken(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token);

    // Validates the token with the given settings and returns the validated JwtSecurityToken.
    private static JwtSecurityToken ValidateToken(string token, JwtSettings settings)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };
        handler.ValidateToken(token, validationParams, out var secToken);
        return (JwtSecurityToken)secToken;
    }

    // 1. The token validates with the same key (no exception means pass).

    [Fact]
    public void Generate_Token_ValidatesWithSameKey()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();

        var (token, _) = generator.Generate(user);

        Action validate = () => ValidateToken(token, settings);
        validate.Should().NotThrow("a freshly generated token must validate with its own signing key");
    }

    // 2. Claims: Sub = user.Id.ToString(), read from JwtSecurityToken.Claims (short names, no mapping).

    [Fact]
    public void Generate_Token_ContainsSubjectClaim_MatchingUserId()
    {
        var generator = BuildGenerator();
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        var parsed = ParseToken(token);

        var sub = parsed.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value;
        sub.Should().NotBeNullOrEmpty();
        sub.Should().Be(user.Id.ToString());
    }

    [Fact]
    public void Generate_Token_ContainsUsernameClaim()
    {
        var generator = BuildGenerator();
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        var parsed = ParseToken(token);

        // "unique_name" is the short form of ClaimTypes.Name registered by JwtSecurityTokenHandler
        var name = parsed.Claims.FirstOrDefault(c =>
            c.Type == ClaimTypes.Name ||
            c.Type == "unique_name" ||
            c.Type == JwtRegisteredClaimNames.UniqueName)?.Value;

        name.Should().Be(user.Username);
    }

    [Fact]
    public void Generate_Token_ContainsEmailClaim()
    {
        var generator = BuildGenerator();
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        var parsed = ParseToken(token);

        var email = parsed.Claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Email)?.Value;
        email.Should().Be(user.Email);
    }

    [Fact]
    public void Generate_Token_ContainsRoleClaim()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        // After validation, ClaimTypes.Role maps correctly
        var validated = ValidateToken(token, settings);

        // "role" is emitted; after handler mapping it becomes ClaimTypes.Role in principal
        // Check directly on the JWT claims (pre-mapping)
        var role = validated.Claims.FirstOrDefault(c =>
            c.Type == ClaimTypes.Role || c.Type == "role")?.Value;
        role.Should().Be(user.Role);
    }

    // 3. Issuer and Audience match configuration.

    [Fact]
    public void Generate_Token_HasCorrectIssuer()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        var parsed = ParseToken(token);

        parsed.Issuer.Should().Be(settings.Issuer);
    }

    [Fact]
    public void Generate_Token_HasCorrectAudience()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();

        var (token, _) = generator.Generate(user);
        var parsed = ParseToken(token);

        parsed.Audiences.Should().Contain(settings.Audience);
    }

    // 4. ExpiresAtUtc is about UtcNow + AccessTokenMinutes.

    [Fact]
    public void Generate_ExpiresAtUtc_ApproximatelyNowPlusMinutes()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();
        var before = DateTime.UtcNow;

        var (_, expiresAtUtc) = generator.Generate(user);

        var after = DateTime.UtcNow;
        var expectedMin = before.AddMinutes(settings.AccessTokenMinutes);
        var expectedMax = after.AddMinutes(settings.AccessTokenMinutes);

        expiresAtUtc.Should().BeOnOrAfter(expectedMin).And.BeOnOrBefore(expectedMax);
    }

    // 5. A token signed with a different key must fail validation.

    [Fact]
    public void Generate_Token_FailsWithWrongKey()
    {
        var settings = TestSettings();
        var generator = BuildGenerator(settings);
        var user = TestUser();

        var (token, _) = generator.Generate(user);

        var wrongSettings = new JwtSettings
        {
            Key = "WrongKey_TotallyDifferentFromReal1234567!",
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            AccessTokenMinutes = settings.AccessTokenMinutes
        };

        Action validate = () => ValidateToken(token, wrongSettings);
        validate.Should().Throw<SecurityTokenSignatureKeyNotFoundException>(
            "a token signed with a different key must not validate");
    }

    // 6. The jti claim is present and unique across calls (anti-replay seed).

    [Fact]
    public void Generate_EachToken_HasUniqueJti()
    {
        var generator = BuildGenerator();
        var user = TestUser();

        var (token1, _) = generator.Generate(user);
        var (token2, _) = generator.Generate(user);

        var jti1 = ParseToken(token1).Id;
        var jti2 = ParseToken(token2).Id;

        jti1.Should().NotBe(jti2, "each token must carry a unique Jti for replay prevention");
    }
}
