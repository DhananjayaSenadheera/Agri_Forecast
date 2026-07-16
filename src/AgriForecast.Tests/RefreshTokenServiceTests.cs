using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the persisted refresh-token (jti / token-family) revocation state machine
/// (RefreshTokenService). A REAL JwtTokenGenerator produces/validates the tokens so the jti travels
/// end-to-end; the store (IRefreshTokenRepository) is mocked so each state transition is asserted in
/// isolation. Pins: issue-persists-row, rotate-marks-used-and-chains-family, reuse-revokes-family,
/// revoked/expired/unknown/crypto-invalid rejected, logout-revokes-family, delete/demote-revokes-user,
/// and fail-closed on a store error.
/// </summary>
public class RefreshTokenServiceTests
{
    private static JwtSettings Settings() => new()
    {
        Key = "TestSuperSecretKey_MustBe16BytesMinimumOrMore!",
        Issuer = "AgriForecast.Tests",
        Audience = "AgriForecast.TestClient",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 7
    };

    private static readonly Guid UserId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    private static (RefreshTokenService svc, Mock<IRefreshTokenRepository> store, JwtTokenGenerator gen) Build()
    {
        var gen = new JwtTokenGenerator(Options.Create(Settings()));
        var store = new Mock<IRefreshTokenRepository>();
        store.Setup(s => s.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        // Compare-and-set default: the caller wins the mark-used race unless a test overrides it.
        store.Setup(s => s.TryMarkUsedAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        store.Setup(s => s.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        store.Setup(s => s.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        store.Setup(s => s.PurgeExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        var svc = new RefreshTokenService(store.Object, gen, Mock.Of<ILogger<RefreshTokenService>>());
        return (svc, store, gen);
    }

    // A refresh token whose jti is KNOWN (so the mocked store can be keyed on it), issued through the
    // real generator so it is genuinely crypto-valid.
    private static (string token, Guid jti) ValidToken(JwtTokenGenerator gen, Guid userId)
    {
        var jti = Guid.NewGuid();
        var (token, _) = gen.GenerateRefreshToken(userId, jti);
        return (token, jti);
    }

    private static RefreshTokenRecord CurrentRow(Guid jti, Guid userId, Guid family) => new()
    {
        Id = Guid.NewGuid(),
        Jti = jti,
        FamilyId = family,
        UserId = userId,
        IssuedAtUtc = DateTime.UtcNow.AddMinutes(-1),
        ExpiresAtUtc = DateTime.UtcNow.AddDays(7),
        UsedAtUtc = null,
        RevokedAtUtc = null
    };

    // ── issue ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IssueNewFamily_PersistsRow_AndReturnsToken()
    {
        var (svc, store, _) = Build();
        RefreshTokenRecord? persisted = null;
        store.Setup(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()))
            .Callback((RefreshTokenRecord r, CancellationToken _) => persisted = r)
            .Returns(Task.CompletedTask);

        var (token, _) = await svc.IssueNewFamilyAsync(UserId);

        token.Should().NotBeNullOrEmpty();
        store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(persisted);
        persisted!.UserId.Should().Be(UserId);
        persisted.UsedAtUtc.Should().BeNull();
        persisted.RevokedAtUtc.Should().BeNull();
        persisted.ExpiresAtUtc.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task IssueNewFamily_StoreError_ReturnsNullToken_DoesNotThrow()
    {
        var (svc, store, _) = Build();
        store.Setup(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var (token, _) = await svc.IssueNewFamilyAsync(UserId);

        token.Should().BeNull("a token that could not be persisted must not be handed out (fail-safe)");
    }

    // ── rotate: happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_ValidCurrentToken_MarksUsed_ChainsFamily_ReturnsNewToken()
    {
        var (svc, store, gen) = Build();
        var family = Guid.NewGuid();
        var (token, jti) = ValidToken(gen, UserId);
        var row = CurrentRow(jti, UserId, family);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>())).ReturnsAsync(row);

        RefreshTokenRecord? child = null;
        store.Setup(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()))
            .Callback((RefreshTokenRecord r, CancellationToken _) => child = r)
            .Returns(Task.CompletedTask);

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeTrue();
        result.UserId.Should().Be(UserId);
        result.Token.Should().NotBeNullOrEmpty();

        // Mark-used goes through the atomic compare-and-set (single conditional UPDATE), never a
        // tracked read-then-write — that is the S1 double-spend guard.
        store.Verify(s => s.TryMarkUsedAsync(jti, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(child);
        child!.FamilyId.Should().Be(family, "the child must chain the SAME family");
        child.Jti.Should().NotBe(jti, "the child must carry a fresh jti");
        child.UserId.Should().Be(UserId);
        store.Verify(s => s.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotate_LosesConcurrentMarkUsedRace_RevokesFamily_NoChild()
    {
        // Two simultaneous rotations of the same jti: the loser's conditional UPDATE affects 0
        // rows. That is a theft signal exactly like a sequential replay — the family must be
        // revoked and NO child minted, otherwise the family forks past reuse detection.
        var (svc, store, gen) = Build();
        var family = Guid.NewGuid();
        var (token, jti) = ValidToken(gen, UserId);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrentRow(jti, UserId, family));
        store.Setup(s => s.TryMarkUsedAsync(jti, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0); // someone else spent it between the read and the update

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.RevokeFamilyAsync(family, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── rotate: reuse detection ────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_AlreadyUsedToken_RevokesEntireFamily_AndFails()
    {
        var (svc, store, gen) = Build();
        var family = Guid.NewGuid();
        var (token, jti) = ValidToken(gen, UserId);
        var used = CurrentRow(jti, UserId, family);
        used.UsedAtUtc = DateTime.UtcNow.AddSeconds(-5); // already rotated once => replay
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>())).ReturnsAsync(used);

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.RevokeFamilyAsync(family, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotate_RevokedToken_Fails_NoChild()
    {
        var (svc, store, gen) = Build();
        var (token, jti) = ValidToken(gen, UserId);
        var revoked = CurrentRow(jti, UserId, Guid.NewGuid());
        revoked.RevokedAtUtc = DateTime.UtcNow.AddSeconds(-5);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>())).ReturnsAsync(revoked);

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── rotate: 401 matrix ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rotate_UnknownJti_FailsClosed()
    {
        var (svc, store, gen) = Build();
        var (token, jti) = ValidToken(gen, UserId);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>())).ReturnsAsync((RefreshTokenRecord?)null);

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotate_ExpiredStoreRow_Fails()
    {
        var (svc, store, gen) = Build();
        var (token, jti) = ValidToken(gen, UserId);
        var expired = CurrentRow(jti, UserId, Guid.NewGuid());
        expired.ExpiresAtUtc = DateTime.UtcNow.AddSeconds(-1);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>())).ReturnsAsync(expired);

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.AddAsync(It.IsAny<RefreshTokenRecord>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public async Task Rotate_CryptoInvalidToken_Fails_WithoutStoreLookup(string? token)
    {
        var (svc, store, _) = Build();

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.GetByJtiAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotate_AccessTokenInCookie_Fails()
    {
        var (svc, store, gen) = Build();
        var (accessToken, _) = gen.Generate(new User
        {
            Id = UserId, Username = "u", Email = "u@t.lk", Role = "Farmer", PasswordHash = "x",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });

        var result = await svc.RotateAsync(accessToken);

        result.IsSuccess.Should().BeFalse();
        store.Verify(s => s.GetByJtiAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Rotate_StoreThrows_FailsClosed()
    {
        var (svc, store, gen) = Build();
        var (token, jti) = ValidToken(gen, UserId);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        var result = await svc.RotateAsync(token);

        result.IsSuccess.Should().BeFalse("a store error must reject the refresh — never a stateless fallback");
    }

    // ── logout ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeFamilyForToken_ValidToken_RevokesFamily()
    {
        var (svc, store, gen) = Build();
        var family = Guid.NewGuid();
        var (token, jti) = ValidToken(gen, UserId);
        store.Setup(s => s.GetByJtiAsync(jti, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CurrentRow(jti, UserId, family));

        await svc.RevokeFamilyForTokenAsync(token);

        store.Verify(s => s.RevokeFamilyAsync(family, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RevokeFamilyForToken_InvalidToken_NoOp()
    {
        var (svc, store, _) = Build();

        await svc.RevokeFamilyForTokenAsync("garbage");

        store.Verify(s => s.GetByJtiAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.RevokeFamilyAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── admin delete / demote ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RevokeAllForUser_DelegatesToStore()
    {
        var (svc, store, _) = Build();

        await svc.RevokeAllForUserAsync(UserId);

        store.Verify(s => s.RevokeAllForUserAsync(UserId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
