using AgriForecast.Application.Requests.Users.Commands.Delete;
using AgriForecast.Application.Requests.Users.Commands.UpdateRole;
using AgriForecast.Application.Requests.Users.Quaries.GetAll;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using UserEntity = AgriForecast.Domain.Entities.User;

namespace AgriForecast.Tests;

/// <summary>
/// API-9 — Admin user-management handlers + validator. Pure unit tests (Moq'd IUserRepository /
/// IUnitofWorkRepository), mirroring the MarketCreateTests style. Covers the fail-closed guards the
/// security review cares about: role whitelist, self-delete, and last-admin delete/demote.
/// </summary>
public class UserManagementHandlerTests
{
    private static UserEntity Farmer(string name = "farmer1") => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Email = $"{name}@test.lk",
        PasswordHash = "hash",
        Role = UserRoles.Farmer,
        CreatedAt = DateTime.UtcNow.AddDays(-2),
        UpdatedAt = DateTime.UtcNow.AddDays(-2)
    };

    private static UserEntity Admin(string name = "admin1") => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Email = $"{name}@test.lk",
        PasswordHash = "hash",
        Role = UserRoles.Admin,
        CreatedAt = DateTime.UtcNow.AddDays(-1),
        UpdatedAt = DateTime.UtcNow.AddDays(-1)
    };

    // ── GetAllUsersQueryHandler ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ProjectsFields_NewestFirst_NoPasswordHash()
    {
        var older = Farmer("old"); older.CreatedAt = DateTime.UtcNow.AddDays(-5);
        var newer = Admin("new");  newer.CreatedAt = DateTime.UtcNow.AddDays(-1);
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[] { older, newer });
        var handler = new GetAllUsersQueryHandler(repo.Object);

        var result = await handler.Handle(new GetAllUsersQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
        result.Data[0].Username.Should().Be("new");   // newest first
        result.Data[0].Role.Should().Be(UserRoles.Admin);
        result.Data[1].Username.Should().Be("old");
        // AdminUserDto has no PasswordHash member at all — the hash cannot leak by construction.
        result.Data[0].GetType().GetProperty("PasswordHash").Should().BeNull();
    }

    [Theory]
    [InlineData(0, 500)]     // page < 1 clamps to 1
    [InlineData(-3, 10)]     // page < 1 clamps to 1
    public async Task GetAll_ClampsPageToAtLeastOne(int page, int pageSize)
    {
        var users = Enumerable.Range(0, 3).Select(i => Farmer($"u{i}")).ToArray();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(users);
        var handler = new GetAllUsersQueryHandler(repo.Object);

        var result = await handler.Handle(new GetAllUsersQuery { Page = page, PageSize = pageSize }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeEmpty(); // page 1 returned, not an empty over-skipped page
    }

    [Fact]
    public async Task GetAll_ClampsPageSizeToMax500()
    {
        var users = Enumerable.Range(0, 5).Select(i => Farmer($"u{i}")).ToArray();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(users);
        var handler = new GetAllUsersQueryHandler(repo.Object);

        // pageSize 100000 must clamp to 500, still returning all 5 rows (page 1).
        var result = await handler.Handle(new GetAllUsersQuery { Page = 1, PageSize = 100_000 }, default);

        result.Data.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetAll_PageSizeBelowOneClampsToOne()
    {
        var users = Enumerable.Range(0, 5).Select(i => Farmer($"u{i}")).ToArray();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(users);
        var handler = new GetAllUsersQueryHandler(repo.Object);

        var result = await handler.Handle(new GetAllUsersQuery { Page = 1, PageSize = 0 }, default);

        result.Data.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAll_EmptyStore_ReturnsEmptySuccess()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<UserEntity>());
        var handler = new GetAllUsersQueryHandler(repo.Object);

        var result = await handler.Handle(new GetAllUsersQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    // ── UpdateUserRoleCommandHandler ───────────────────────────────────────────────

    private static (UpdateUserRoleCommandHandler handler, Mock<IUserRepository> repo, Mock<IUnitofWorkRepository> uow, Mock<IRefreshTokenService> refresh)
        BuildUpdate()
    {
        var repo = new Mock<IUserRepository>();
        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);
        var refresh = new Mock<IRefreshTokenService>();
        refresh.Setup(r => r.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var handler = new UpdateUserRoleCommandHandler(
            repo.Object, uow.Object, refresh.Object, Mock.Of<ILogger<UpdateUserRoleCommandHandler>>());
        return (handler, repo, uow, refresh);
    }

    [Fact]
    public async Task UpdateRole_PromoteFarmerToAdmin_Succeeds_Commits()
    {
        var (handler, repo, uow, refresh) = BuildUpdate();
        var target = Farmer();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = target.Id, Role = UserRoles.Admin, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data!.Role.Should().Be(UserRoles.Admin);
        target.Role.Should().Be(UserRoles.Admin);
        repo.Verify(r => r.UpdateAsync(target), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
        // Role changed → the user's refresh families are revoked so they re-authenticate.
        refresh.Verify(r => r.RevokeAllForUserAsync(target.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateRole_DemoteAdmin_WhenOtherAdminsExist_Succeeds()
    {
        var (handler, repo, uow, refresh) = BuildUpdate();
        var target = Admin();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(2);

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = target.Id, Role = UserRoles.Farmer, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeTrue();
        target.Role.Should().Be(UserRoles.Farmer);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateRole_DemoteLastAdmin_Fails_NoCommit()
    {
        var (handler, repo, uow, refresh) = BuildUpdate();
        var target = Admin();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(1);

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = target.Id, Role = UserRoles.Farmer, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("last remaining admin");
        target.Role.Should().Be(UserRoles.Admin); // unchanged
        repo.Verify(r => r.UpdateAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdateRole_TargetNotFound_Fails()
    {
        var (handler, repo, uow, refresh) = BuildUpdate();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((UserEntity?)null);

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = Guid.NewGuid(), Role = UserRoles.Admin, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("admin")]   // wrong case is NOT the canonical "Admin"
    [InlineData("")]
    public async Task UpdateRole_InvalidRole_Fails_NoLookup(string role)
    {
        var (handler, repo, uow, refresh) = BuildUpdate();

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = Guid.NewGuid(), Role = role, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdateRole_NoOp_SameRole_SucceedsWithoutCommit()
    {
        var (handler, repo, uow, refresh) = BuildUpdate();
        var target = Farmer();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);

        var result = await handler.Handle(new UpdateUserRoleCommand
        {
            TargetUserId = target.Id, Role = UserRoles.Farmer, ActingUserId = Guid.NewGuid()
        }, default);

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
        // Idempotent no-op must NOT churn refresh sessions.
        refresh.Verify(r => r.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── DeleteUserCommandHandler ───────────────────────────────────────────────────

    private static (DeleteUserCommandHandler handler, Mock<IUserRepository> repo, Mock<IUnitofWorkRepository> uow, Mock<IRefreshTokenService> refresh)
        BuildDelete()
    {
        var repo = new Mock<IUserRepository>();
        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);
        var refresh = new Mock<IRefreshTokenService>();
        refresh.Setup(r => r.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var handler = new DeleteUserCommandHandler(
            repo.Object, uow.Object, refresh.Object, Mock.Of<ILogger<DeleteUserCommandHandler>>());
        return (handler, repo, uow, refresh);
    }

    [Fact]
    public async Task Delete_Self_Fails_NoDelete()
    {
        var (handler, repo, uow, refresh) = BuildDelete();
        var me = Guid.NewGuid();

        var result = await handler.Handle(new DeleteUserCommand(me, me), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("your own account");
        repo.Verify(r => r.DeleteAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
        // A rejected delete must not revoke anything.
        refresh.Verify(r => r.RevokeAllForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_TargetNotFound_Fails()
    {
        var (handler, repo, uow, refresh) = BuildDelete();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((UserEntity?)null);

        var result = await handler.Handle(new DeleteUserCommand(Guid.NewGuid(), Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("not found");
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Delete_LastAdmin_Fails_NoDelete()
    {
        var (handler, repo, uow, refresh) = BuildDelete();
        var target = Admin();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(1);

        var result = await handler.Handle(new DeleteUserCommand(target.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("last remaining admin");
        repo.Verify(r => r.DeleteAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Delete_Admin_WhenOthersExist_Succeeds()
    {
        var (handler, repo, uow, refresh) = BuildDelete();
        var target = Admin();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(3);

        var result = await handler.Handle(new DeleteUserCommand(target.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(target), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
        // Delete revokes all of the target's refresh families before removing the user.
        refresh.Verify(r => r.RevokeAllForUserAsync(target.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_Farmer_Succeeds_NoAdminCountCheck()
    {
        var (handler, repo, uow, refresh) = BuildDelete();
        var target = Farmer();
        repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);

        var result = await handler.Handle(new DeleteUserCommand(target.Id, Guid.NewGuid()), default);

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.DeleteAsync(target), Times.Once);
        repo.Verify(r => r.CountByRoleAsync(It.IsAny<string>()), Times.Never); // farmer delete skips the guard
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    // ── UpdateUserRoleCommandValidator ─────────────────────────────────────────────

    private readonly UpdateUserRoleCommandValidator _validator = new();

    [Theory]
    [InlineData("Admin")]
    [InlineData("Farmer")]
    public async Task Validator_AllowsWhitelistedRoles(string role)
    {
        var result = await _validator.ValidateAsync(new UpdateUserRoleCommand
        {
            TargetUserId = Guid.NewGuid(), ActingUserId = Guid.NewGuid(), Role = role
        });
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("admin")]
    [InlineData("FARMER")]
    [InlineData("")]
    public async Task Validator_RejectsNonWhitelistedRoles(string role)
    {
        var result = await _validator.ValidateAsync(new UpdateUserRoleCommand
        {
            TargetUserId = Guid.NewGuid(), ActingUserId = Guid.NewGuid(), Role = role
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateUserRoleCommand.Role));
    }

    [Fact]
    public async Task Validator_RejectsEmptyTargetAndActingIds()
    {
        var result = await _validator.ValidateAsync(new UpdateUserRoleCommand
        {
            TargetUserId = Guid.Empty, ActingUserId = Guid.Empty, Role = UserRoles.Admin
        });
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateUserRoleCommand.TargetUserId));
        result.Errors.Should().Contain(e => e.PropertyName == nameof(UpdateUserRoleCommand.ActingUserId));
    }
}
