using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;
using AgriForecast.Application.Requests.Forecast.Validators;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

// GET /api/forecast/crop/{cropId}/harvest-window (best harvest window, UI 2026-07-25).
//
// The handler is a strict passthrough over the ML service. The load-bearing rule it
// enforces is the SUCCESS/FAILURE split, and both directions are traps:
//   * rankable=false is a SUCCESS. It is the honest "we cannot tell one planting
//     date from another for this crop" answer and the UI has a state for it.
//     Turning it into a Failure would show a farmer a scary error for a normal,
//     expected situation (a crop the model does not yet serve).
//   * null (ML unreachable) is a FAILURE. Dressing it up as an empty-but-successful
//     window would tell a farmer "there is no good time to plant" when the truth is
//     "we could not ask".
public class HarvestWindowHandlerTests
{
    private static readonly Guid CropId = Guid.NewGuid();

    private static (GetHarvestWindowQueryHandler handler, Mock<IHarvestPredictionClient> clientMock) BuildHandler()
    {
        var clientMock = new Mock<IHarvestPredictionClient>();
        var handler = new GetHarvestWindowQueryHandler(
            clientMock.Object, Mock.Of<ILogger<GetHarvestWindowQueryHandler>>());
        return (handler, clientMock);
    }

    private static HarvestWindowDto RankableWindow() => new()
    {
        CropId = CropId,
        CropName = "Tomato",
        AsOf = new DateOnly(2026, 7, 25),
        GrowthPeriodDays = 70,
        Rankable = true,
        ReasonCode = "ml_served",
        ActivePredictor = "residual",
        Confidence = "Medium",
        ModelVersion = "v17",
        Explanation = "Compares planting dates using season and festival demand.",
        WindowDays = 14,
        Points = new List<HarvestWindowPointDto>
        {
            new()
            {
                PlantDate = new DateOnly(2026, 8, 12),
                HarvestDate = new DateOnly(2026, 10, 21),
                PredictedPrice = 268m, LowerBound = 198m, UpperBound = 341m,
                InBestWindow = true,
            },
            new()
            {
                PlantDate = new DateOnly(2026, 9, 20),
                HarvestDate = new DateOnly(2026, 11, 29),
                PredictedPrice = 212m, LowerBound = 160m, UpperBound = 280m,
                InBestWindow = false,
            },
        },
        Best = new HarvestWindowBestDto
        {
            PlantStart = new DateOnly(2026, 8, 12),
            PlantEnd = new DateOnly(2026, 8, 26),
            HarvestStart = new DateOnly(2026, 10, 21),
            HarvestEnd = new DateOnly(2026, 11, 4),
            PredictedPrice = 268m, LowerBound = 198m, UpperBound = 341m,
            UpliftPct = 7.2m,
        },
    };

    [Fact]
    public async Task Passes_the_rankable_window_through_verbatim()
    {
        var (handler, clientMock) = BuildHandler();
        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, null, 90, default))
            .ReturnsAsync(RankableWindow());

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        var data = result.Data!;
        Assert.True(data.Rankable);
        Assert.Equal("ml_served", data.ReasonCode);
        Assert.Equal(14, data.WindowDays);
        Assert.Equal(2, data.Points.Count);
        Assert.Equal(new DateOnly(2026, 8, 12), data.Best!.PlantStart);
        Assert.Equal(new DateOnly(2026, 11, 4), data.Best.HarvestEnd);
        Assert.Equal(7.2m, data.Best.UpliftPct);
        // Exactly the points the ML flagged — the handler must not re-derive them.
        Assert.Single(data.Points, p => p.InBestWindow);
    }

    [Theory]
    [InlineData("flat_curve")]
    [InlineData("crop_not_model_served")]
    [InlineData("no_growth_period")]
    [InlineData("model_inactive")]
    [InlineData("no_feature_row")]
    [InlineData("scoring_failed")]
    public async Task Not_rankable_is_a_success_with_its_reason(string reasonCode)
    {
        // Every one of these is a NORMAL outcome the UI renders as "we cannot rank
        // dates for this crop yet" — not an error state.
        var (handler, clientMock) = BuildHandler();
        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, null, 90, default))
            .ReturnsAsync(new HarvestWindowDto
            {
                CropId = CropId,
                Rankable = false,
                ReasonCode = reasonCode,
                ActivePredictor = "unavailable",
                Confidence = "Low",
                Explanation = "There is no better or worse time to aim for.",
            });

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Rankable);
        Assert.Equal(reasonCode, result.Data.ReasonCode);
        Assert.Empty(result.Data.Points);
        Assert.Null(result.Data.Best);
        Assert.NotEmpty(result.Data.Explanation);
    }

    [Fact]
    public async Task Transport_failure_is_a_failure()
    {
        var (handler, clientMock) = BuildHandler();
        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, null, 90, default))
            .ReturnsAsync((HarvestWindowDto?)null);

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, HorizonDays = 90 }, default);

        Assert.False(result.IsSuccess);
        Assert.Contains("unavailable", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwards_as_of_and_horizon_to_the_ml_service()
    {
        var (handler, clientMock) = BuildHandler();
        var asOf = new DateOnly(2026, 3, 1);
        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 120, default))
            .ReturnsAsync(RankableWindow())
            .Verifiable();

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 120 },
            default);

        Assert.True(result.IsSuccess);
        clientMock.Verify();
    }

    [Fact]
    public void Default_horizon_is_90_days()
    {
        Assert.Equal(90, new GetHarvestWindowQuery().HorizonDays);
    }
}

// The validator mirrors the Python route's own bounds so the two layers can never
// disagree about what a legal sweep is.
public class GetHarvestWindowValidatorTests
{
    private static readonly GetHarvestWindowValidator Validator = new();

    [Fact]
    public void Empty_crop_id_is_rejected()
    {
        var result = Validator.Validate(new GetHarvestWindowQuery { CropId = Guid.Empty, HorizonDays = 90 });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(GetHarvestWindowQuery.CropId));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(0)]
    [InlineData(366)]
    [InlineData(-1)]
    public void Out_of_range_horizon_is_rejected(int horizonDays)
    {
        var result = Validator.Validate(new GetHarvestWindowQuery { CropId = Guid.NewGuid(), HorizonDays = horizonDays });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(GetHarvestWindowQuery.HorizonDays));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(90)]
    [InlineData(365)]
    public void In_range_horizon_is_accepted(int horizonDays)
    {
        var result = Validator.Validate(new GetHarvestWindowQuery { CropId = Guid.NewGuid(), HorizonDays = horizonDays });
        Assert.True(result.IsValid);
    }
}
