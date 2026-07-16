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
/// Tests for the stateless refresh-token support on JwtTokenGenerator (API-8):
/// GenerateRefreshToken + ValidateRefreshToken, and the token-type separation that keeps a
/// refresh token out of the access-token (Bearer) pipeline and an access token out of /refresh.
/// </summary>
public class RefreshTokenGeneratorTests
{
    private static JwtSettings TestSettings() => new()
    {
        Key = "TestSuperSecretKey_MustBe16BytesMinimumOrMore!",
        Issuer = "AgriForecast.Tests",
        Audience = "AgriForecast.TestClient",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 7
        // RefreshAudience left empty => derives to "AgriForecast.TestClient.refresh"
    };

    private static readonly Guid TestUserId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static JwtTokenGenerator BuildGenerator(JwtSettings? settings = null)
        => new(Options.Create(settings ?? TestSettings()));

    private static User TestUser() => new()
    {
        Id = TestUserId,
        Username = "farmer_test",
        Email = "farmer@test.lk",
        Role = "Farmer",
        PasswordHash = "irrelevant",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static JwtSecurityToken Parse(string token)
        => new JwtSecurityTokenHandler().ReadJwtToken(token);

    // ── generation ────────────────────────────────────────────────────────────────

    [Fact]
    public void GenerateRefreshToken_CarriesRefreshTokenUseClaim()
    {
        var (token, _) = BuildGenerator().GenerateRefreshToken(TestUserId);
        var use = Parse(token).Claims.FirstOrDefault(c => c.Type == "token_use")?.Value;
        use.Should().Be("refresh", "the refresh token must be marked as a refresh token");
    }

    [Fact]
    public void GenerateRefreshToken_UsesDistinctRefreshAudience()
    {
        var settings = TestSettings();
        var (token, _) = BuildGenerator(settings).GenerateRefreshToken(TestUserId);
        var parsed = Parse(token);

        parsed.Audiences.Should().Contain("AgriForecast.TestClient.refresh");
        parsed.Audiences.Should().NotContain(settings.Audience,
            "refresh audience must differ from the access audience so the Bearer pipeline rejects it");
    }

    [Fact]
    public void GenerateRefreshToken_ExpiresRoughlyRefreshTokenDaysFromNow()
    {
        var settings = TestSettings();
        var before = DateTime.UtcNow;
        var (_, expiresAtUtc) = BuildGenerator(settings).GenerateRefreshToken(TestUserId);
        var after = DateTime.UtcNow;

        expiresAtUtc.Should().BeOnOrAfter(before.AddDays(settings.RefreshTokenDays))
            .And.BeOnOrBefore(after.AddDays(settings.RefreshTokenDays));
    }

    [Fact]
    public void GenerateRefreshToken_SubjectIsUserId_AndCarriesNoUsername()
    {
        var (token, _) = BuildGenerator().GenerateRefreshToken(TestUserId);
        var claims = Parse(token).Claims.ToList();

        // Subject is the immutable id, lowercased.
        claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sub)?.Value
            .Should().Be(TestUserId.ToString());

        // Username must be kept out of the refresh token entirely (S1).
        claims.Should().NotContain(c => c.Type == "unique_name" || c.Type == "name",
            "the refresh token must not embed the mutable username");
    }

    // ── validation happy path ───────────────────────────────────────────────────────

    [Fact]
    public void ValidateRefreshToken_ValidToken_ReturnsUserId()
    {
        var gen = BuildGenerator();
        var (token, _) = gen.GenerateRefreshToken(TestUserId);

        gen.ValidateRefreshToken(token).Should().Be(TestUserId.ToString());
    }

    // ── jti round-trip (revocation-store support) ────────────────────────────────────

    [Fact]
    public void GenerateRefreshToken_WithSuppliedJti_EmbedsThatJti()
    {
        var gen = BuildGenerator();
        var jti = Guid.NewGuid();
        var (token, _) = gen.GenerateRefreshToken(TestUserId, jti);

        // The JWT id claim must be exactly the supplied jti so the persisted record and the token agree.
        Parse(token).Id.Should().Be(jti.ToString());
    }

    [Fact]
    public void ReadValidatedRefreshToken_ValidToken_ReturnsUserIdAndJti()
    {
        var gen = BuildGenerator();
        var jti = Guid.NewGuid();
        var (token, _) = gen.GenerateRefreshToken(TestUserId, jti);

        var principal = gen.ReadValidatedRefreshToken(token);

        Assert.NotNull(principal);
        principal!.UserId.Should().Be(TestUserId);
        principal.Jti.Should().Be(jti);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void ReadValidatedRefreshToken_MissingOrGarbage_ReturnsNull(string? token)
    {
        Assert.Null(BuildGenerator().ReadValidatedRefreshToken(token));
    }

    [Fact]
    public void ReadValidatedRefreshToken_AccessToken_ReturnsNull()
    {
        var gen = BuildGenerator();
        var (accessToken, _) = gen.Generate(TestUser());

        Assert.Null(gen.ReadValidatedRefreshToken(accessToken));
    }

    // ── validation 401 matrix ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("aaa.bbb.ccc")]
    public void ValidateRefreshToken_MissingOrGarbage_ReturnsNull(string? token)
    {
        BuildGenerator().ValidateRefreshToken(token).Should().BeNull();
    }

    [Fact]
    public void ValidateRefreshToken_AccessTokenInCookie_ReturnsNull()
    {
        // An access token (wrong audience, no token_use=refresh) must be rejected by /refresh.
        var gen = BuildGenerator();
        var (accessToken, _) = gen.Generate(TestUser());

        gen.ValidateRefreshToken(accessToken).Should().BeNull();
    }

    [Fact]
    public void ValidateRefreshToken_ExpiredToken_ReturnsNull()
    {
        var settings = TestSettings();
        var expired = ExpiredRefreshToken(settings, TestUserId);

        BuildGenerator(settings).ValidateRefreshToken(expired).Should().BeNull();
    }

    // Builds a well-formed refresh token that is already expired (notBefore + expires in the past),
    // which the generator itself can't produce (it stamps notBefore = now).
    internal static string ExpiredRefreshToken(JwtSettings settings, Guid userId)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim("token_use", "refresh")
        };
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.EffectiveRefreshAudience,
            claims,
            notBefore: DateTime.UtcNow.AddDays(-2),
            expires: DateTime.UtcNow.AddDays(-1),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public void ValidateRefreshToken_WrongSigningKey_ReturnsNull()
    {
        var (token, _) = BuildGenerator().GenerateRefreshToken(TestUserId);

        var otherKey = new JwtSettings
        {
            Key = "AnotherCompletelyDifferentSigningKey_32Bytes+!",
            Issuer = "AgriForecast.Tests",
            Audience = "AgriForecast.TestClient",
            RefreshTokenDays = 7
        };
        BuildGenerator(otherKey).ValidateRefreshToken(token).Should().BeNull();
    }

    // ── token-type separation: refresh token rejected by the ACCESS (Bearer) pipeline ─

    [Fact]
    public void RefreshToken_RejectedByAccessTokenValidation()
    {
        // Mirrors exactly what the JwtBearer middleware does on a protected endpoint:
        // ValidateAudience against the ACCESS audience. A refresh token (refresh audience) fails.
        var settings = TestSettings();
        var (refreshToken, _) = BuildGenerator(settings).GenerateRefreshToken(TestUserId);

        var handler = new JwtSecurityTokenHandler();
        var accessValidation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Issuer,
            ValidateAudience = true,
            ValidAudience = settings.Audience, // access audience
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero
        };

        Action asBearer = () => handler.ValidateToken(refreshToken, accessValidation, out _);
        asBearer.Should().Throw<SecurityTokenInvalidAudienceException>(
            "a refresh token presented as a Bearer access token must be rejected (=> 401)");
    }
}
