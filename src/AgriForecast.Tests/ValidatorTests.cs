using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;
using AgriForecast.Application.Requests.Forecast.Validators;
using FluentAssertions;
using FluentValidation;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for GetHarvestForecastValidator and GetCropTimelineValidator.
/// These validators are invoked by the MediatR validation behavior pipeline.
/// We test them directly — no need to spin up MediatR.
/// </summary>
public class ValidatorTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // GetHarvestForecastValidator
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly GetHarvestForecastValidator _harvestValidator = new();

    [Fact]
    public async Task HarvestValidator_EmptyGuid_Fails()
    {
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.Empty,
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CropId");
    }

    [Fact]
    public async Task HarvestValidator_ValidGuidAndPlantDate_Passes()
    {
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.NewGuid(),
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow)
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task HarvestValidator_PlantDateTooFarInFuture_Fails()
    {
        // 366 days into the future = beyond MaxFutureDays (365)
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.NewGuid(),
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(366))
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "PlantDate");
    }

    [Fact]
    public async Task HarvestValidator_PlantDateTooFarInPast_Fails()
    {
        // 731 days in the past = beyond MaxPastDays (730)
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.NewGuid(),
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-731))
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "PlantDate");
    }

    [Fact]
    public async Task HarvestValidator_PlantDateAtMaxPastBoundary_Passes()
    {
        // Exactly 730 days back = right at the boundary, should pass.
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.NewGuid(),
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-730))
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue("exactly 730 days in the past is the allowed boundary");
    }

    [Fact]
    public async Task HarvestValidator_PlantDateAtMaxFutureBoundary_Passes()
    {
        var query = new GetHarvestForecastQuery
        {
            CropId = Guid.NewGuid(),
            PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(365))
        };

        var result = await _harvestValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue("exactly 365 days in the future is the allowed boundary");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // GetCropTimelineValidator
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly GetCropTimelineValidator _timelineValidator = new();

    [Fact]
    public async Task TimelineValidator_EmptyGuid_Fails()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.Empty,
            Months = 12
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CropId");
    }

    [Fact]
    public async Task TimelineValidator_Months_Zero_Fails()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.NewGuid(),
            Months = 0
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Months");
    }

    [Fact]
    public async Task TimelineValidator_Months_25_Fails()
    {
        // MaxMonths = 24, so 25 should be rejected.
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.NewGuid(),
            Months = 25
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "Months");
    }

    [Fact]
    public async Task TimelineValidator_Months_1_Passes()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.NewGuid(),
            Months = 1
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TimelineValidator_Months_24_Passes()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.NewGuid(),
            Months = 24
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TimelineValidator_ValidInputs_Passes()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.NewGuid(),
            Months = 12
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task TimelineValidator_BothInvalid_BothErrorsReported()
    {
        var query = new GetCropTimelineQuery
        {
            CropId = Guid.Empty,
            Months = 0
        };

        var result = await _timelineValidator.ValidateAsync(query);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "CropId");
        result.Errors.Should().Contain(e => e.PropertyName == "Months");
    }
}
