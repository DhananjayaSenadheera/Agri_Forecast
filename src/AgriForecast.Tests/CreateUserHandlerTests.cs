using AgriForecast.Application.Requests.Users.Commands.Create;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using UserEntity = AgriForecast.Domain.Entities.User;

namespace AgriForecast.Tests;

/// <summary>
/// Admin-only user creation (POST /api/users/create). Pure unit tests with mocked repository, unit of
/// work, hasher and audit. The cases that matter: the role whitelist is re-checked in the handler, the
/// uniqueness guards match the self-registration path exactly, the raw password never reaches the entity,
/// and — the reason this command exists — no token or refresh cookie is produced, so creating a user can
/// never disturb the acting admin's own session.
/// </summary>
public class CreateUserHandlerTests
{
    private static (CreateUserCommandHandler handler, Mock<IUserRepository> repo, Mock<IUnitofWorkRepository> uow,
        Mock<IPasswordHasher> hasher, Mock<IUserActivityAudit> audit) Build()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((UserEntity?)null);
        repo.Setup(r => r.GetByEmailAsync(It.IsAny<string>())).ReturnsAsync((UserEntity?)null);
        repo.Setup(r => r.AddAsync(It.IsAny<UserEntity>())).ReturnsAsync((UserEntity u) => u);

        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);

        var hasher = new Mock<IPasswordHasher>();
        hasher.Setup(h => h.Hash(It.IsAny<string>())).Returns((string p) => $"hashed:{p}");

        var audit = new Mock<IUserActivityAudit>();
        audit.Setup(a => a.RecordUserCreatedByAdminAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var handler = new CreateUserCommandHandler(
            repo.Object, uow.Object, hasher.Object, audit.Object,
            Mock.Of<ILogger<CreateUserCommandHandler>>());
        return (handler, repo, uow, hasher, audit);
    }

    private static CreateUserCommand Cmd(string role = UserRoles.Farmer, string username = "newfarmer") => new()
    {
        Username = username,
        Email = $"{username}@test.lk",
        Password = "correct-horse-battery",
        Role = role,
        ActingUserId = Guid.NewGuid()
    };

    private static UserEntity Existing(string name) => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Email = $"{name}@test.lk",
        PasswordHash = "hash",
        Role = UserRoles.Farmer,
        CreatedAt = DateTime.UtcNow.AddDays(-3),
        UpdatedAt = DateTime.UtcNow.AddDays(-3)
    };

    // Happy paths.

    [Theory]
    [InlineData(UserRoles.Farmer)]
    [InlineData(UserRoles.Admin)]
    public async Task Create_WithAssignableRole_Succeeds_Commits(string role)
    {
        var (handler, repo, uow, _, _) = Build();
        UserEntity? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<UserEntity>()))
            .Callback<UserEntity>(u => added = u)
            .ReturnsAsync((UserEntity u) => u);

        var result = await handler.Handle(Cmd(role), default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Role.Should().Be(role);
        result.Data.Username.Should().Be("newfarmer");
        Assert.NotNull(added);
        added!.Role.Should().Be(role);
        added.Id.Should().NotBe(Guid.Empty);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Create_HashesPassword_NeverStoresOrReturnsRaw()
    {
        var (handler, repo, _, hasher, _) = Build();
        UserEntity? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<UserEntity>()))
            .Callback<UserEntity>(u => added = u)
            .ReturnsAsync((UserEntity u) => u);

        var result = await handler.Handle(Cmd(), default);

        hasher.Verify(h => h.Hash("correct-horse-battery"), Times.Once);
        added!.PasswordHash.Should().Be("hashed:correct-horse-battery");
        added.PasswordHash.Should().NotBe("correct-horse-battery");
        // AdminUserDto has no PasswordHash member at all — the hash cannot leak by construction.
        result.Data!.GetType().GetProperty("PasswordHash").Should().BeNull();
    }

    [Fact]
    public async Task Create_StampsCreatedAndUpdatedTogether()
    {
        var (handler, repo, _, _, _) = Build();
        UserEntity? added = null;
        repo.Setup(r => r.AddAsync(It.IsAny<UserEntity>()))
            .Callback<UserEntity>(u => added = u)
            .ReturnsAsync((UserEntity u) => u);

        await handler.Handle(Cmd(), default);

        added!.CreatedAt.Should().Be(added.UpdatedAt);
        added.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    // The whole reason this command is not Auth.Register.

    [Fact]
    public async Task Create_ReturnsNoTokenOrSession_OnlyTheAdminProjection()
    {
        var (handler, _, _, _, _) = Build();

        var result = await handler.Handle(Cmd(), default);

        // The returned type is the admin list projection — it has no token/expiry surface at all, so
        // the controller has nothing it could turn into a refresh cookie for the acting admin.
        var dtoType = result.Data!.GetType();
        dtoType.GetProperty("AccessToken").Should().BeNull();
        dtoType.GetProperty("ExpiresAtUtc").Should().BeNull();
    }

    [Fact]
    public async Task Create_AuditsActingAdminAsActorAndNewUserAsTarget()
    {
        var (handler, _, _, _, audit) = Build();
        var cmd = Cmd();

        var result = await handler.Handle(cmd, default);

        // Actor = the admin who created it, target = the new account. A self-registration records
        // the new user as the actor with NO target, so the trail keeps the two apart.
        audit.Verify(a => a.RecordUserCreatedByAdminAsync(
            cmd.ActingUserId, result.Data!.Id, It.IsAny<CancellationToken>()), Times.Once);
        audit.Verify(a => a.RecordUserRegisteredAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Fail-closed guards.

    [Theory]
    [InlineData("Superuser")]
    [InlineData("admin")]   // wrong case is NOT the canonical "Admin"
    [InlineData("")]
    public async Task Create_InvalidRole_Fails_NoLookupNoWrite(string role)
    {
        var (handler, repo, uow, _, _) = Build();

        var result = await handler.Handle(Cmd(role), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Invalid role");
        repo.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);
        repo.Verify(r => r.AddAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Create_DuplicateUsername_Fails_NoWrite()
    {
        var (handler, repo, uow, _, _) = Build();
        repo.Setup(r => r.GetByUsernameAsync("newfarmer")).ReturnsAsync(Existing("newfarmer"));

        var result = await handler.Handle(Cmd(), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("already taken");
        repo.Verify(r => r.AddAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Create_DuplicateEmail_Fails_NoWrite()
    {
        var (handler, repo, uow, _, _) = Build();
        repo.Setup(r => r.GetByEmailAsync("newfarmer@test.lk")).ReturnsAsync(Existing("someoneelse"));

        var result = await handler.Handle(Cmd(), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("already registered");
        repo.Verify(r => r.AddAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Create_FailedGuard_WritesNoAuditRow()
    {
        var (handler, repo, _, _, audit) = Build();
        repo.Setup(r => r.GetByUsernameAsync("newfarmer")).ReturnsAsync(Existing("newfarmer"));

        await handler.Handle(Cmd(), default);

        audit.Verify(a => a.RecordUserCreatedByAdminAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // CreateUserCommandValidator.

    private readonly CreateUserCommandValidator _validator = new();

    [Theory]
    [InlineData(UserRoles.Admin)]
    [InlineData(UserRoles.Farmer)]
    public async Task Validator_AllowsWhitelistedRoles(string role)
    {
        var result = await _validator.ValidateAsync(Cmd(role));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("admin")]
    [InlineData("FARMER")]
    [InlineData("")]
    public async Task Validator_RejectsNonWhitelistedRoles(string role)
    {
        var result = await _validator.ValidateAsync(Cmd(role));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.Role));
    }

    [Theory]
    [InlineData("short1")]                  // 6 chars — under the 8 minimum
    [InlineData("")]                        // empty
    public async Task Validator_RejectsWeakOrMissingPassword(string password)
    {
        var cmd = Cmd();
        cmd.Password = password;
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.Password));
    }

    [Fact]
    public async Task Validator_RejectsPasswordOver128Chars()
    {
        var cmd = Cmd();
        cmd.Password = new string('x', 129);
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.Password));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task Validator_RejectsBadEmail(string email)
    {
        var cmd = Cmd();
        cmd.Email = email;
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.Email));
    }

    [Fact]
    public async Task Validator_RejectsEmptyUsernameAndOverlongUsername()
    {
        var empty = Cmd();
        empty.Username = "";
        (await _validator.ValidateAsync(empty)).IsValid.Should().BeFalse();

        var overlong = Cmd();
        overlong.Username = new string('u', 51);
        var result = await _validator.ValidateAsync(overlong);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.Username));
    }

    [Fact]
    public async Task Validator_RejectsEmptyActingUserId()
    {
        var cmd = Cmd();
        cmd.ActingUserId = Guid.Empty;
        var result = await _validator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(CreateUserCommand.ActingUserId));
    }
}
