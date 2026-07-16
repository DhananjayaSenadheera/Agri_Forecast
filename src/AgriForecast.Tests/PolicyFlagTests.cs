using AgriForecast.Application.Requests.PolicyFlag;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Delete;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Update;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;
using AgriForecast.Application.Requests.PolicyFlag.Validators;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for the PolicyFlag vertical slice:
///   - PolicyFlagCreateCommandValidator (date-window rules)
///   - PolicyFlagGetAllQueryHandler (all vs as-of routing)
/// Style mirrors ValidatorTests / handler tests already in this project.
/// </summary>
public class PolicyFlagTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Validator
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly PolicyFlagCreateCommandValidator _validator = new();

    private static PolicyFlagCreateCommand ValidCommand() => new()
    {
        PolicyFlagCreateDto = new PolicyFlag_CreateDto
        {
            PolicyType = PolicyType.ImportBan,
            Title = "Test policy",
            Description = "desc",
            EffectiveFrom = new DateTime(2022, 01, 01),
            EffectiveTo = new DateTime(2022, 06, 30),
            Direction = PolicyDirection.Bullish,
            Source = "Gov",
            ReferenceUrl = null
        }
    };

    [Fact]
    public async Task Validator_ValidCommand_Passes()
    {
        var result = await _validator.ValidateAsync(ValidCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validator_MissingTitle_Fails()
    {
        var cmd = ValidCommand();
        cmd.PolicyFlagCreateDto.Title = "";

        var result = await _validator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Title"));
    }

    [Fact]
    public async Task Validator_MissingEffectiveFrom_Fails()
    {
        var cmd = ValidCommand();
        cmd.PolicyFlagCreateDto.EffectiveFrom = default;

        var result = await _validator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("EffectiveFrom"));
    }

    [Fact]
    public async Task Validator_EffectiveToBeforeEffectiveFrom_Fails()
    {
        var cmd = ValidCommand();
        cmd.PolicyFlagCreateDto.EffectiveFrom = new DateTime(2022, 06, 01);
        cmd.PolicyFlagCreateDto.EffectiveTo = new DateTime(2022, 01, 01);

        var result = await _validator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("EffectiveTo"));
    }

    [Fact]
    public async Task Validator_NullEffectiveTo_Passes()
    {
        // null EffectiveTo means "still in effect" — must be allowed.
        var cmd = ValidCommand();
        cmd.PolicyFlagCreateDto.EffectiveTo = null;

        var result = await _validator.ValidateAsync(cmd);

        result.IsValid.Should().BeTrue("a null EffectiveTo represents an open-ended, still-active policy");
    }

    [Fact]
    public async Task Validator_EqualEffectiveToAndFrom_Passes()
    {
        var cmd = ValidCommand();
        cmd.PolicyFlagCreateDto.EffectiveFrom = new DateTime(2022, 03, 01);
        cmd.PolicyFlagCreateDto.EffectiveTo = new DateTime(2022, 03, 01);

        var result = await _validator.ValidateAsync(cmd);

        result.IsValid.Should().BeTrue("a single-day policy window is valid");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // GetAll query handler
    // ──────────────────────────────────────────────────────────────────────────────

    private static PolicyFlag Flag(string title, DateTime from, DateTime? to) => new()
    {
        Id = Guid.NewGuid(),
        PolicyType = PolicyType.Other,
        Title = title,
        EffectiveFrom = from,
        EffectiveTo = to,
        Direction = PolicyDirection.Neutral,
        CreatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task GetAll_NoAsOfDate_ReturnsAllFromGetAllAsync()
    {
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetAllAsync())
            .ReturnsAsync(new[]
            {
                Flag("a", new DateTime(2021, 1, 1), null),
                Flag("b", new DateTime(2022, 1, 1), new DateTime(2022, 6, 1))
            });

        var handler = new PolicyFlagGetAllQueryHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagGetAllQueryHandler>>());

        var result = await handler.Handle(new PolicyFlagGetAllQuery { AsOfDate = null }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
        repo.Verify(r => r.GetAllAsync(), Times.Once);
        repo.Verify(r => r.GetActiveAsOfAsync(It.IsAny<DateTime>()), Times.Never);
    }

    [Fact]
    public async Task GetAll_WithAsOfDate_RoutesToGetActiveAsOf()
    {
        var asOf = new DateTime(2021, 06, 01);
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetActiveAsOfAsync(asOf))
            .ReturnsAsync(new[] { Flag("active", new DateTime(2021, 1, 1), null) });

        var handler = new PolicyFlagGetAllQueryHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagGetAllQueryHandler>>());

        var result = await handler.Handle(new PolicyFlagGetAllQuery { AsOfDate = asOf }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle();
        repo.Verify(r => r.GetActiveAsOfAsync(asOf), Times.Once);
        repo.Verify(r => r.GetAllAsync(), Times.Never);
    }

    [Fact]
    public async Task GetAll_Empty_ReturnsFailure()
    {
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<PolicyFlag>());

        var handler = new PolicyFlagGetAllQueryHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagGetAllQueryHandler>>());

        var result = await handler.Handle(new PolicyFlagGetAllQuery(), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // API-13: Update validator (mirrors create + Id required + Source REQUIRED on mutation)
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly PolicyFlagUpdateCommandValidator _updateValidator = new();

    private static PolicyFlagUpdateCommand ValidUpdateCommand() => new()
    {
        PolicyFlagUpdateDto = new PolicyFlag_UpdateDto
        {
            Id = Guid.NewGuid(),
            PolicyType = PolicyType.ImportBan,
            Title = "Edited policy",
            Description = "desc",
            EffectiveFrom = new DateTime(2022, 01, 01),
            EffectiveTo = new DateTime(2022, 06, 30),
            Direction = PolicyDirection.Bullish,
            Source = "Gazette 2231/45",
            ReferenceUrl = null
        }
    };

    [Fact]
    public async Task UpdateValidator_ValidCommand_Passes()
    {
        var result = await _updateValidator.ValidateAsync(ValidUpdateCommand());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateValidator_MissingId_Fails()
    {
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.Id = Guid.Empty;

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Id"));
    }

    [Fact]
    public async Task UpdateValidator_MissingSource_Fails()
    {
        // Source is optional on create but REQUIRED on mutation (citation for training-data edits).
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.Source = "";

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Source"));
    }

    [Fact]
    public async Task UpdateValidator_MissingTitle_Fails()
    {
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.Title = "";

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Title"));
    }

    [Fact]
    public async Task UpdateValidator_InvalidPolicyType_Fails()
    {
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.PolicyType = (PolicyType)999;

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("PolicyType"));
    }

    [Fact]
    public async Task UpdateValidator_InvalidDirection_Fails()
    {
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.Direction = (PolicyDirection)7;

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Direction"));
    }

    [Fact]
    public async Task UpdateValidator_EffectiveToBeforeFrom_Fails()
    {
        var cmd = ValidUpdateCommand();
        cmd.PolicyFlagUpdateDto.EffectiveFrom = new DateTime(2022, 06, 01);
        cmd.PolicyFlagUpdateDto.EffectiveTo = new DateTime(2022, 01, 01);

        var result = await _updateValidator.ValidateAsync(cmd);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("EffectiveTo"));
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // API-13: Update handler
    // ──────────────────────────────────────────────────────────────────────────────

    private static (Mock<IPolicyFlagRepository> repo, Mock<IUnitofWorkRepository> uow) MutationMocks()
        => (new Mock<IPolicyFlagRepository>(), new Mock<IUnitofWorkRepository>());

    private static PolicyFlagUpdateCommandHandler UpdateHandler(
        Mock<IPolicyFlagRepository> repo, Mock<IUnitofWorkRepository> uow)
        => new(repo.Object, Mock.Of<ILogger<PolicyFlagUpdateCommandHandler>>(), uow.Object);

    private static PolicyFlag_UpdateDto UpdateDto(Guid id, DateTime from, DateTime? to = null) => new()
    {
        Id = id,
        PolicyType = PolicyType.ImportBan,
        Title = "Edited",
        Description = "d",
        EffectiveFrom = from,
        EffectiveTo = to,
        Direction = PolicyDirection.Bullish,
        Source = "Gazette",
        ReferenceUrl = null
    };

    [Fact]
    public async Task Update_NotFound_ReturnsFailure()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((PolicyFlag?)null);

        var result = await UpdateHandler(repo, uow).Handle(
            new PolicyFlagUpdateCommand { PolicyFlagUpdateDto = UpdateDto(id, new DateTime(2100, 1, 1)) },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        repo.Verify(r => r.UpdateAsync(It.IsAny<PolicyFlag>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Update_PastWindow_SucceedsWithTrainingWarning()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        var existing = Flag("old", new DateTime(2020, 1, 1), null);
        existing.Id = id;
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(existing);

        // New window also in the past.
        var result = await UpdateHandler(repo, uow).Handle(
            new PolicyFlagUpdateCommand { PolicyFlagUpdateDto = UpdateDto(id, new DateTime(2021, 3, 1), new DateTime(2021, 6, 1)) },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Id.Should().Be(id);
        result.Data.TrainingDataWarning.Should().NotBeNullOrEmpty();
        repo.Verify(r => r.UpdateAsync(It.IsAny<PolicyFlag>()), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Update_FutureOnlyWindow_SucceedsWithNoWarning()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        // Previous window ALSO in the future, so nothing has touched training history.
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(Flag("old", new DateTime(2099, 1, 1), null));

        var result = await UpdateHandler(repo, uow).Handle(
            new PolicyFlagUpdateCommand { PolicyFlagUpdateDto = UpdateDto(id, new DateTime(2100, 1, 1)) },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().BeNull();
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Update_MovingFutureFlagIntoPast_Warns()
    {
        // Previous window future, new window past -> the edit pulls the flag into training history.
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(Flag("old", new DateTime(2099, 1, 1), null));

        var result = await UpdateHandler(repo, uow).Handle(
            new PolicyFlagUpdateCommand { PolicyFlagUpdateDto = UpdateDto(id, new DateTime(2020, 1, 1)) },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Update_HappyPath_AppliesFieldsAndNormalisesDates()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        var existing = Flag("old", new DateTime(2099, 1, 1), null);
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(existing);

        var dto = UpdateDto(id, new DateTime(2100, 5, 10, 13, 45, 0), new DateTime(2100, 9, 1, 8, 0, 0));
        dto.Title = "New title";

        var result = await UpdateHandler(repo, uow).Handle(
            new PolicyFlagUpdateCommand { PolicyFlagUpdateDto = dto }, default);

        result.IsSuccess.Should().BeTrue();
        existing.Title.Should().Be("New title");
        existing.EffectiveFrom.Should().Be(new DateTime(2100, 5, 10)); // time stripped
        existing.EffectiveTo.Should().Be(new DateTime(2100, 9, 1));
        existing.EffectiveFrom.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // API-13: Delete handler
    // ──────────────────────────────────────────────────────────────────────────────

    private static PolicyFlagDeleteCommandHandler DeleteHandler(
        Mock<IPolicyFlagRepository> repo, Mock<IUnitofWorkRepository> uow)
        => new(repo.Object, Mock.Of<ILogger<PolicyFlagDeleteCommandHandler>>(), uow.Object);

    [Fact]
    public async Task Delete_EmptyId_ReturnsFailure()
    {
        var (repo, uow) = MutationMocks();

        var result = await DeleteHandler(repo, uow).Handle(new PolicyFlagDeleteCommand(Guid.Empty), default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Delete_NotFound_ReturnsFailure()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync((PolicyFlag?)null);

        var result = await DeleteHandler(repo, uow).Handle(new PolicyFlagDeleteCommand(id), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
        repo.Verify(r => r.DeleteAsync(It.IsAny<PolicyFlag>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Delete_PastWindow_SucceedsWithTrainingWarning()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        var existing = Flag("old", new DateTime(2020, 1, 1), null);
        existing.Id = id;
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(existing);

        var result = await DeleteHandler(repo, uow).Handle(new PolicyFlagDeleteCommand(id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Id.Should().Be(id);
        result.Data.TrainingDataWarning.Should().NotBeNullOrEmpty();
        repo.Verify(r => r.DeleteAsync(existing), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Delete_FutureOnlyWindow_SucceedsWithNoWarning()
    {
        var (repo, uow) = MutationMocks();
        var id = Guid.NewGuid();
        repo.Setup(r => r.GetByIdAsync(id)).ReturnsAsync(Flag("old", new DateTime(2099, 1, 1), null));

        var result = await DeleteHandler(repo, uow).Handle(new PolicyFlagDeleteCommand(id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().BeNull();
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // API-13: training-data warning helper (pure logic)
    // ──────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 07, 16);

    [Fact]
    public void Warning_PastEffectiveFrom_ReturnsMessage()
    {
        PolicyFlagTrainingDataWarning.For(new DateTime(2020, 1, 1), null, Now)
            .Should().Be(PolicyFlagTrainingDataWarning.Message);
    }

    [Fact]
    public void Warning_FutureEffectiveFrom_ReturnsNull()
    {
        PolicyFlagTrainingDataWarning.For(new DateTime(2030, 1, 1), null, Now)
            .Should().BeNull();
    }

    [Fact]
    public void Warning_Today_ReturnsNull()
    {
        // "Past" is strictly before today; a window starting today has not yet fed a training run.
        PolicyFlagTrainingDataWarning.For(Now, null, Now).Should().BeNull();
    }

    [Fact]
    public void Warning_FutureNewButPastPrevious_ReturnsMessage()
    {
        PolicyFlagTrainingDataWarning.For(new DateTime(2030, 1, 1), new DateTime(2020, 1, 1), Now)
            .Should().Be(PolicyFlagTrainingDataWarning.Message);
    }
}
