using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Market.Commands.Create;
using AgriForecast.Application.Requests.Market.DTOs;
using AgriForecast.Application.Requests.Market.Validators;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using MarketEntity = AgriForecast.Domain.Entities.Market;

namespace AgriForecast.Tests;

/// <summary>
/// R2 D-DF3 (subtask 3.2) — the Market create registration path that replaces the retired
/// EconomicCenters CRUD stack. Covers MarketCreateValidator (NotEmpty name/district, valid
/// MarketType) and MarketCreateCommandHandler (code stamped once via CodeSettings, flag threaded,
/// commit called). "Register a new economic centre" = create with IsEconomicCenter = true.
/// </summary>
public class MarketCreateTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // MarketCreateValidator
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly MarketCreateValidator _validator = new();

    private static MarketCreateCommand Cmd(
        string name = "Nuwara Eliya DEC",
        string? district = "Nuwara Eliya",
        MarketType type = MarketType.DEC,
        bool isEco = false) => new()
    {
        CreateDto = new Market_CreateDto
        {
            Name = name,
            District = district,
            MarketType = type,
            IsEconomicCenter = isEco
        }
    };

    [Fact]
    public async Task Validator_ValidCommand_Passes()
    {
        var result = await _validator.ValidateAsync(Cmd());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validator_EmptyName_Fails()
    {
        var result = await _validator.ValidateAsync(Cmd(name: ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CreateDto.Name");
    }

    [Fact]
    public async Task Validator_EmptyDistrict_Fails()
    {
        var result = await _validator.ValidateAsync(Cmd(district: ""));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CreateDto.District");
    }

    [Fact]
    public async Task Validator_UndefinedMarketType_Fails()
    {
        var result = await _validator.ValidateAsync(Cmd(type: (MarketType)99));
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CreateDto.MarketType");
    }

    [Fact]
    public async Task Validator_EconomicCentreRegistration_Passes()
    {
        // Registering an economic centre is a valid market create with the flag set.
        var result = await _validator.ValidateAsync(Cmd(isEco: true));
        result.IsValid.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // MarketCreateCommandHandler
    // ──────────────────────────────────────────────────────────────────────────────

    private static (MarketCreateCommandHandler handler,
                    Mock<IGenericRepository<MarketEntity>> repo,
                    Mock<IUnitofWorkRepository> uow,
                    List<MarketEntity> captured)
        BuildHandler(string mktPrefix = "MKT", int mktCode = 7, int mktPadding = 8)
    {
        var settings = new DefaultSetting
        {
            Id = 1,
            Crop_Prefix = "CROP", Crop_Padding = 8, Crop_Code = 1,
            Mkt_Prefix = mktPrefix, Mkt_Padding = mktPadding, Mkt_Code = mktCode
        };
        var settingRepo = new Mock<IDefaultSettingRepository>();
        settingRepo.Setup(r => r.GetDefaultSetting()).ReturnsAsync(settings);
        var codeSettings = new CodeSettings(settingRepo.Object);

        var captured = new List<MarketEntity>();
        var repo = new Mock<IGenericRepository<MarketEntity>>();
        repo.Setup(r => r.AddAsync(It.IsAny<MarketEntity>()))
            .Callback<MarketEntity>(captured.Add)
            .Returns(Task.CompletedTask);

        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);

        var handler = new MarketCreateCommandHandler(
            codeSettings, repo.Object, uow.Object,
            Mock.Of<ILogger<MarketCreateCommandHandler>>());

        return (handler, repo, uow, captured);
    }

    [Fact]
    public async Task Handler_ValidCommand_StampsCode_Persists_Commits()
    {
        var (handler, repo, uow, captured) = BuildHandler(mktCode: 7);

        var result = await handler.Handle(Cmd(), default);

        result.IsSuccess.Should().BeTrue();
        captured.Should().ContainSingle();
        // Mkt_Code 7 + padding 8 → MKT00000007.
        captured[0].MarketCode.Should().Be("MKT00000007");
        repo.Verify(r => r.AddAsync(It.IsAny<MarketEntity>()), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task Handler_EconomicCentre_ThreadsFlagTrue()
    {
        var (handler, _, _, captured) = BuildHandler();

        var result = await handler.Handle(Cmd(isEco: true), default);

        result.IsSuccess.Should().BeTrue();
        captured.Should().ContainSingle();
        captured[0].IsEconomicCenter.Should().BeTrue("registering an economic centre sets the flag");
    }

    [Fact]
    public async Task Handler_PlainMarket_FlagDefaultsFalse()
    {
        var (handler, _, _, captured) = BuildHandler();

        await handler.Handle(Cmd(isEco: false), default);

        captured.Should().ContainSingle();
        captured[0].IsEconomicCenter.Should().BeFalse();
    }

    [Fact]
    public async Task Handler_NullDto_FailsWithoutPersisting()
    {
        var (handler, repo, uow, _) = BuildHandler();

        var result = await handler.Handle(new MarketCreateCommand { CreateDto = null! }, default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.AddAsync(It.IsAny<MarketEntity>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task Handler_CopiesNameDistrictAndType()
    {
        var (handler, _, _, captured) = BuildHandler();

        await handler.Handle(Cmd(name: "Pettah", district: "Colombo", type: MarketType.Wholesale), default);

        captured.Should().ContainSingle();
        captured[0].Name.Should().Be("Pettah");
        captured[0].District.Should().Be("Colombo");
        captured[0].MarketType.Should().Be(MarketType.Wholesale);
    }
}
