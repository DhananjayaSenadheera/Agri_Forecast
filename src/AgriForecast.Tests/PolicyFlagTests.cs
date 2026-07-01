using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
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
}
