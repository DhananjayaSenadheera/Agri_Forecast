using AgriForecast.API.Startup;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using UserEntity = AgriForecast.Domain.Entities.User;

namespace AgriForecast.Tests;

/// <summary>
/// API-9 — config-driven admin bootstrap. Verifies the one-time promotion path and every no-op path
/// (config absent, admin already exists, named user missing). The hosted service opens a DI scope,
/// so tests wire a real ServiceCollection around mocked repositories.
/// </summary>
public class AdminBootstrapTests
{
    private static UserEntity Farmer(string name = "owner") => new()
    {
        Id = Guid.NewGuid(),
        Username = name,
        Email = $"{name}@test.lk",
        PasswordHash = "hash",
        Role = UserRoles.Farmer,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    private static AdminBootstrapHostedService Build(
        Mock<IUserRepository> repo,
        Mock<IUnitofWorkRepository> uow,
        string? bootstrapUsername)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => repo.Object);
        services.AddScoped(_ => uow.Object);
        var provider = services.BuildServiceProvider();

        var configPairs = new Dictionary<string, string?>();
        if (bootstrapUsername is not null)
            configPairs[AdminBootstrapHostedService.ConfigKey] = bootstrapUsername;
        var config = new ConfigurationBuilder().AddInMemoryCollection(configPairs).Build();

        return new AdminBootstrapHostedService(
            provider, config, Mock.Of<ILogger<AdminBootstrapHostedService>>());
    }

    [Fact]
    public async Task NoConfig_DoesNothing()
    {
        var repo = new Mock<IUserRepository>();
        var uow = new Mock<IUnitofWorkRepository>();
        var svc = Build(repo, uow, bootstrapUsername: null);

        await svc.StartAsync(default);

        repo.Verify(r => r.CountByRoleAsync(It.IsAny<string>()), Times.Never);
        repo.Verify(r => r.UpdateAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task BlankConfig_DoesNothing()
    {
        var repo = new Mock<IUserRepository>();
        var uow = new Mock<IUnitofWorkRepository>();
        var svc = Build(repo, uow, bootstrapUsername: "   ");

        await svc.StartAsync(default);

        repo.Verify(r => r.CountByRoleAsync(It.IsAny<string>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task AdminAlreadyExists_DoesNotPromote()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(1);
        var uow = new Mock<IUnitofWorkRepository>();
        var svc = Build(repo, uow, bootstrapUsername: "owner");

        await svc.StartAsync(default);

        repo.Verify(r => r.GetByUsernameAsync(It.IsAny<string>()), Times.Never);
        repo.Verify(r => r.UpdateAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task NoAdmin_UserExists_PromotesExactlyOnce()
    {
        var owner = Farmer("owner");
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(0);
        repo.Setup(r => r.GetByUsernameAsync("owner")).ReturnsAsync(owner);
        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);
        var svc = Build(repo, uow, bootstrapUsername: "owner");

        await svc.StartAsync(default);

        owner.Role.Should().Be(UserRoles.Admin);
        repo.Verify(r => r.UpdateAsync(owner), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task NoAdmin_UserExists_TrimsUsername()
    {
        var owner = Farmer("owner");
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(0);
        repo.Setup(r => r.GetByUsernameAsync("owner")).ReturnsAsync(owner);
        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);
        var svc = Build(repo, uow, bootstrapUsername: "  owner  ");

        await svc.StartAsync(default);

        repo.Verify(r => r.GetByUsernameAsync("owner"), Times.Once);
        owner.Role.Should().Be(UserRoles.Admin);
    }

    [Fact]
    public async Task NoAdmin_UserNotFound_NoPromotion()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.CountByRoleAsync(UserRoles.Admin)).ReturnsAsync(0);
        repo.Setup(r => r.GetByUsernameAsync(It.IsAny<string>())).ReturnsAsync((UserEntity?)null);
        var uow = new Mock<IUnitofWorkRepository>();
        var svc = Build(repo, uow, bootstrapUsername: "ghost");

        await svc.StartAsync(default);

        repo.Verify(r => r.UpdateAsync(It.IsAny<UserEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task RepositoryThrows_StartupNotCrashed()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.CountByRoleAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        var uow = new Mock<IUnitofWorkRepository>();
        var svc = Build(repo, uow, bootstrapUsername: "owner");

        // Must NOT throw — a failed bootstrap can never take down startup.
        var act = async () => await svc.StartAsync(default);
        await act.Should().NotThrowAsync();
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }
}
