using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AgriForecast.Application.Requests.Auth.Commands.Refresh;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for RefreshCommandHandler. Uses a REAL JwtTokenGenerator (so validation is exercised
/// end-to-end) plus a mocked IUserRepository. Covers the happy path and the full 401 matrix.
/// </summary>
public class RefreshCommandHandlerTests
{
    private static JwtSettings Settings() => new()
    {
        Key = "TestSuperSecretKey_MustBe16BytesMinimumOrMore!",
        Issuer = "AgriForecast.Tests",
        Audience = "AgriForecast.TestClient",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 7
    };

    private static readonly User StoredUser = new()
    {
        Id = Guid.NewGuid(),
        Username = "farmer_test",
        Email = "farmer@test.lk",
        Role = "Farmer",
        PasswordHash = "irrelevant",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static (RefreshCommandHandler handler, JwtTokenGenerator gen) Build(
        User? userReturnedByRepo)
    {
        var gen = new JwtTokenGenerator(Options.Create(Settings()));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>()))
            .ReturnsAsync(userReturnedByRepo);
        var handler = new RefreshCommandHandler(
            repo.Object, gen, Mock.Of<ILogger<RefreshCommandHandler>>());
        return (handler, gen);
    }

    [Fact]
    public async Task Handle_ValidRefreshToken_ReturnsFreshAccessToken()
    {
        var gen = new JwtTokenGenerator(Options.Create(Settings()));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(StoredUser);
        var handler = new RefreshCommandHandler(
            repo.Object, gen, Mock.Of<ILogger<RefreshCommandHandler>>());
        var (refreshToken, _) = gen.GenerateRefreshToken(StoredUser.Id);

        var result = await handler.Handle(
            new RefreshCommand { RefreshToken = refreshToken }, default);

        // Resolution must key on the immutable id from the refresh token, never the username.
        repo.Verify(r => r.GetByIdAsync(StoredUser.Id), Times.Once);
        repo.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);

        result.IsSuccess.Should().BeTrue();
        result.Data.Username.Should().Be(StoredUser.Username);
        result.Data.Email.Should().Be(StoredUser.Email);
        result.Data.Role.Should().Be(StoredUser.Role);
        result.Data.AccessToken.Should().NotBeNullOrWhiteSpace();
        result.Data.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);

        // The issued access token must carry the user's id as subject (access-token shape).
        var parsed = new JwtSecurityTokenHandler().ReadJwtToken(result.Data.AccessToken);
        parsed.Claims.First(c => c.Type == JwtRegisteredClaimNames.Sub).Value
            .Should().Be(StoredUser.Id.ToString());
        parsed.Audiences.Should().Contain(Settings().Audience);
        // Access token must NOT be a refresh token.
        parsed.Claims.Any(c => c.Type == "token_use").Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage.token.value")]
    public async Task Handle_MissingOrGarbageToken_Fails(string? token)
    {
        var (handler, _) = Build(StoredUser);

        var result = await handler.Handle(new RefreshCommand { RefreshToken = token }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired refresh token.");
    }

    [Fact]
    public async Task Handle_AccessTokenInCookie_Fails()
    {
        var (handler, gen) = Build(StoredUser);
        var (accessToken, _) = gen.Generate(StoredUser);

        var result = await handler.Handle(
            new RefreshCommand { RefreshToken = accessToken }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired refresh token.");
    }

    [Fact]
    public async Task Handle_ExpiredToken_Fails()
    {
        var expiredToken = RefreshTokenGeneratorTests.ExpiredRefreshToken(Settings(), StoredUser.Id);

        var (handler, _) = Build(StoredUser);
        var result = await handler.Handle(
            new RefreshCommand { RefreshToken = expiredToken }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired refresh token.");
    }

    [Fact]
    public async Task Handle_ValidTokenButUserDeleted_Fails()
    {
        // Signature is valid, but the account no longer exists in the store.
        var (handler, gen) = Build(userReturnedByRepo: null);
        var (refreshToken, _) = gen.GenerateRefreshToken(StoredUser.Id);

        var result = await handler.Handle(
            new RefreshCommand { RefreshToken = refreshToken }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("Invalid or expired refresh token.");
    }
}
