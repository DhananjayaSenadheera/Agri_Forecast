using System.Data.Common;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;
using AgriForecast.Application.Requests.Forecast.Validators;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
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
//
// The one number the handler adds is CurrentPrice, and it exists to stop the panel
// recommending a window the forecast screen simultaneously calls a loss.
public class HarvestWindowHandlerTests
{
    private static readonly Guid CropId = Guid.NewGuid();

    private static (GetHarvestWindowQueryHandler handler,
                    Mock<IHarvestPredictionClient> clientMock,
                    Mock<IMarketPriceRepository> repoMock) BuildHandler()
    {
        var clientMock = new Mock<IHarvestPredictionClient>();
        var repoMock = new Mock<IMarketPriceRepository>();

        // Default: no recent prices. Tests that care about the current price override this.
        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<DateOnly>(), default))
            .ReturnsAsync(new List<MarketPrice>());

        var handler = new GetHarvestWindowQueryHandler(
            clientMock.Object, repoMock.Object, Mock.Of<ILogger<GetHarvestWindowQueryHandler>>());
        return (handler, clientMock, repoMock);
    }

    // Daily rows as (min, max); the rule averages the mid (min+max)/2 across them.
    private static List<MarketPrice> Prices(params (decimal Min, decimal Max)[] rows) =>
        rows.Select((r, i) => new MarketPrice
        {
            Id = Guid.NewGuid(),
            CropId = CropId,
            MinPrice = r.Min,
            MaxPrice = r.Max,
            PriceDate = new DateOnly(2026, 7, 25).AddDays(-i),
            Source = "test",
        }).ToList();

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
        var (handler, clientMock, _) = BuildHandler();
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
        var (handler, clientMock, _) = BuildHandler();
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
        var (handler, clientMock, _) = BuildHandler();
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
        var (handler, clientMock, _) = BuildHandler();
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

    // Current price: the panel shows this beside the window's expected price so the farmer can see whether
    // the recommendation actually beats selling today.

    [Fact]
    public async Task Current_price_is_the_trailing_average_of_the_daily_mid()
    {
        var (handler, clientMock, repoMock) = BuildHandler();
        var asOf = new DateOnly(2026, 7, 25);

        // Mids 100, 110, 121 → 110.333... → 110.33 (2dp). Min != Max on purpose:
        // it must be the MID that is averaged, not either bound.
        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(CropId, CurrentPriceRule.TrailingRows, asOf, default))
            .ReturnsAsync(Prices((90m, 110m), (100m, 120m), (111m, 131m)))
            .Verifiable();

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 90, default))
            .ReturnsAsync(RankableWindow());

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(110.33m, result.Data!.CurrentPrice);
        // Pinned: the 14-row window is what stops one noisy market day flipping a verdict.
        Assert.Equal(14, CurrentPriceRule.TrailingRows);
        repoMock.Verify();
    }

    [Fact]
    public async Task Current_price_is_read_as_of_today_when_the_query_omits_it()
    {
        // Same no-lookahead reason as the forecast screen, and the same date the
        // sweep starts from — a window priced against tomorrow's data is not advice.
        var (handler, clientMock, repoMock) = BuildHandler();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, null, 90, default))
            .ReturnsAsync(RankableWindow());

        await handler.Handle(new GetHarvestWindowQuery { CropId = CropId, HorizonDays = 90 }, default);

        repoMock.Verify(
            r => r.GetRecentByCropIdAsync(CropId, CurrentPriceRule.TrailingRows, today, default),
            Times.Once);
    }

    [Fact]
    public async Task Current_price_is_forwarded_when_the_window_is_not_rankable()
    {
        // The comparison is still worth showing when we cannot rank planting dates —
        // "here is what it fetches today" is honest, useful, and not a ranking claim.
        var (handler, clientMock, repoMock) = BuildHandler();
        var asOf = new DateOnly(2026, 7, 25);

        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(CropId, CurrentPriceRule.TrailingRows, asOf, default))
            .ReturnsAsync(Prices((180m, 220m)));

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 90, default))
            .ReturnsAsync(new HarvestWindowDto
            {
                CropId = CropId,
                Rankable = false,
                ReasonCode = "flat_curve",
                ActivePredictor = "unavailable",
                Confidence = "Low",
                Explanation = "There is no better or worse time to aim for.",
            });

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        Assert.False(result.Data!.Rankable);
        Assert.Equal(200m, result.Data.CurrentPrice);
    }

    [Fact]
    public async Task No_recent_prices_yield_zero_and_still_a_successful_window()
    {
        // 0 means "unknown" and the UI hides the comparison. Inventing a price would
        // be worse than showing none, and failing the whole window over a missing
        // side-by-side number would hide advice we DO have.
        var (handler, clientMock, repoMock) = BuildHandler();
        var asOf = new DateOnly(2026, 7, 25);

        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(CropId, CurrentPriceRule.TrailingRows, asOf, default))
            .ReturnsAsync(new List<MarketPrice>());

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 90, default))
            .ReturnsAsync(RankableWindow());

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Data!.CurrentPrice);
        Assert.True(result.Data.Rankable);
        Assert.NotNull(result.Data.Best);
    }

    // Stand-in for a SqlException (DbException is abstract and cannot be newed up).
    private sealed class FakeDbException : DbException
    {
        public FakeDbException() : base("database unreachable") { }
    }

    public static TheoryData<Exception> InfrastructureFaults() => new()
    {
        new FakeDbException(),          // DB unreachable / transient fault
        new TimeoutException("timed out"),
    };

    [Theory]
    [MemberData(nameof(InfrastructureFaults))]
    public async Task A_failed_price_lookup_still_returns_the_window(Exception fault)
    {
        // The window is the payload; the price is decoration. Throwing away good
        // planting advice because a secondary lookup fell over is the worse failure.
        var (handler, clientMock, repoMock) = BuildHandler();
        var asOf = new DateOnly(2026, 7, 25);

        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(CropId, It.IsAny<int>(), asOf, default))
            .ThrowsAsync(fault);

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 90, default))
            .ReturnsAsync(RankableWindow());

        var result = await handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 90 }, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(0m, result.Data!.CurrentPrice);   // degraded to "unknown"
        Assert.True(result.Data.Rankable);
        Assert.NotNull(result.Data.Best);
        Assert.Equal(2, result.Data.Points.Count);
    }

    [Fact]
    public async Task A_programming_error_in_the_price_lookup_is_not_swallowed()
    {
        // Guards the catch filter against being widened: a bug must stay loud, not
        // masquerade as a crop with no recent prices.
        var (handler, clientMock, repoMock) = BuildHandler();

        repoMock
            .Setup(r => r.GetRecentByCropIdAsync(CropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
            .ThrowsAsync(new NullReferenceException("bug"));

        clientMock
            .Setup(c => c.GetHarvestWindowAsync(CropId, null, 90, default))
            .ReturnsAsync(RankableWindow());

        await Assert.ThrowsAsync<NullReferenceException>(() => handler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, HorizonDays = 90 }, default));
    }

    [Fact]
    public async Task Quotes_the_same_todays_price_as_the_harvest_forecast_screen()
    {
        // THE REASON THIS FIELD EXISTS: the two screens are read minutes apart. If
        // they derived "today's price" separately, one could recommend a window the
        // other calls "below today's price". Same rows in, same number out.
        var asOf = new DateOnly(2026, 7, 25);
        var rows = Prices((90m, 110m), (100m, 120m), (111m, 131m));

        var (windowHandler, windowClient, windowRepo) = BuildHandler();
        windowRepo
            .Setup(r => r.GetRecentByCropIdAsync(CropId, It.IsAny<int>(), asOf, default))
            .ReturnsAsync(rows);
        windowClient
            .Setup(c => c.GetHarvestWindowAsync(CropId, asOf, 90, default))
            .ReturnsAsync(RankableWindow());

        var forecastRepo = new Mock<IMarketPriceRepository>();
        forecastRepo
            .Setup(r => r.GetRecentByCropIdAsync(CropId, It.IsAny<int>(), asOf, default))
            .ReturnsAsync(rows);
        var forecastClient = new Mock<IHarvestPredictionClient>();
        forecastClient
            .Setup(c => c.PredictAsync(CropId, asOf, default))
            .ReturnsAsync(new HarvestPredictionDto
            {
                CropId = CropId.ToString(),
                CropName = "Tomato",
                PlantDate = "2026-07-25",
                HarvestDate = "2026-10-03",
                GrowthPeriodDays = 70,
                PredictedPrice = 268m,
                LowerBound = 198m,
                UpperBound = 341m,
                Confidence = "Medium",
                ActivePredictor = "residual",
                ModelVersion = "v17",
                Explanation = "Test",
            });
        var forecastHandler = new GetHarvestForecastQueryHandler(
            forecastRepo.Object, forecastClient.Object,
            Mock.Of<ILogger<GetHarvestForecastQueryHandler>>());

        var window = await windowHandler.Handle(
            new GetHarvestWindowQuery { CropId = CropId, AsOf = asOf, HorizonDays = 90 }, default);
        var forecast = await forecastHandler.Handle(
            new GetHarvestForecastQuery { CropId = CropId, PlantDate = asOf }, default);

        Assert.True(window.IsSuccess);
        Assert.True(forecast.IsSuccess);
        Assert.Equal(forecast.Data!.CurrentPrice, window.Data!.CurrentPrice);
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

    // AsOf anchors the current-price query as well as the sweep, so a typo'd date
    // would quietly price the window against nothing.

    [Fact]
    public void Omitted_as_of_is_accepted()
    {
        // The normal case: no AsOf means today.
        var result = Validator.Validate(new GetHarvestWindowQuery { CropId = Guid.NewGuid(), HorizonDays = 90 });
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-730)]
    [InlineData(365)]
    public void In_range_as_of_is_accepted(int offsetDays)
    {
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays);
        var result = Validator.Validate(
            new GetHarvestWindowQuery { CropId = Guid.NewGuid(), AsOf = asOf, HorizonDays = 90 });
        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData(-731)]
    [InlineData(366)]
    [InlineData(-40000)]
    public void Out_of_range_as_of_is_rejected(int offsetDays)
    {
        var asOf = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays);
        var result = Validator.Validate(
            new GetHarvestWindowQuery { CropId = Guid.NewGuid(), AsOf = asOf, HorizonDays = 90 });
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.PropertyName == nameof(GetHarvestWindowQuery.AsOf));
    }
}
