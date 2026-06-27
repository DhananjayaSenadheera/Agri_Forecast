using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for the recommendation matrix inside GetHarvestForecastQueryHandler.
/// The private Recommend() method is tested indirectly via Handle(), which is the
/// correct seam — we do not couple tests to private method signatures.
/// </summary>
public class RecommendationMatrixTests
{
    // Builds a minimal valid HarvestPredictionDto with controllable fields.
    private static HarvestPredictionDto MakePrediction(
        decimal predicted,
        decimal lower,
        decimal upper,
        string confidence = "High",
        string activePredictor = "model") => new()
    {
        CropId = Guid.NewGuid().ToString(),
        CropName = "Tomato",
        PlantDate = "2026-01-01",
        HarvestDate = "2026-04-01",
        GrowthPeriodDays = 90,
        PredictedPrice = predicted,
        LowerBound = lower,
        UpperBound = upper,
        Confidence = confidence,
        ActivePredictor = activePredictor,
        ModelVersion = "v1",
        Explanation = "Test"
    };

    // Wires up the handler with a mock market-price repo and a mock prediction client.
    private static (GetHarvestForecastQueryHandler handler,
                    Mock<IMarketPriceRepository> repMock,
                    Mock<IHarvestPredictionClient> clientMock)
        BuildHandler()
    {
        var repMock = new Mock<IMarketPriceRepository>();
        var clientMock = new Mock<IHarvestPredictionClient>();
        var logger = Mock.Of<ILogger<GetHarvestForecastQueryHandler>>();
        var handler = new GetHarvestForecastQueryHandler(repMock.Object, clientMock.Object, logger);
        return (handler, repMock, clientMock);
    }

    // Helper: a single-price current-price list.
    private static List<MarketPrice> PriceList(decimal mid, DateOnly date) =>
        new()
        {
            new MarketPrice
            {
                Id = Guid.NewGuid(),
                CropId = (Guid?)Guid.NewGuid(),  // Guid? nullable field
                MinPrice = mid,
                MaxPrice = mid,
                PriceDate = date,
                Source = "test"
            }
        };

    private static GetHarvestForecastQuery MakeQuery(Guid cropId) => new()
    {
        CropId = cropId,
        PlantDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)) // yesterday = valid
    };

    // ──────────────────────────────────────────────────────────────────────────────
    // 1. Invalid-input guards
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_ZeroCurrentPrice_ReturnsNotRecommended()
    {
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(new List<MarketPrice>()); // no prices → currentPrice = 0

        var prediction = MakePrediction(100m, 80m, 120m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
        result.Data.LowTrust.Should().BeTrue();
    }

    [Fact]
    public async Task Recommend_ZeroPredictedPrice_ReturnsNotRecommended()
    {
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // predicted = 0 → guard fires
        var prediction = MakePrediction(0m, 0m, 0m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
    }

    [Fact]
    public async Task Recommend_InvalidInterval_LowerAbovePredicted_ReturnsNotRecommended()
    {
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // lower > predicted → invalid interval
        var prediction = MakePrediction(predicted: 100m, lower: 120m, upper: 140m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
        result.Data.LowTrust.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 2. Upside bands
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_UpsideBelow5pct_Down_ReturnsNotRecommended()
    {
        // currentPrice=100, predicted=94 → upside=-6% → NotRecommended
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        var prediction = MakePrediction(predicted: 94m, lower: 80m, upper: 108m); // width=28%
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
    }

    [Fact]
    public async Task Recommend_UpsideFlat_ReturnsRecommendedWithRisk()
    {
        // currentPrice=100, predicted=104 → upside=4% (0-8% band) → RecommendedWithRisk
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // tight interval: width = 20/104 ≈ 19%
        var prediction = MakePrediction(predicted: 104m, lower: 94m, upper: 114m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.RecommendedWithRisk);
    }

    [Fact]
    public async Task Recommend_Upside10pct_TightInterval_ReturnsRecommended()
    {
        // currentPrice=100, predicted=110 → upside=10% (8-20% band), tight interval (width ≤ 30%)
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // width = (121-99)/110 = 22/110 ≈ 20%
        var prediction = MakePrediction(predicted: 110m, lower: 99m, upper: 121m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.Recommended);
    }

    [Fact]
    public async Task Recommend_Upside25pct_TightInterval_ReturnsStronglyRecommended()
    {
        // currentPrice=100, predicted=125 → upside=25% (≥20%), tight interval → StronglyRecommended
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // width = (137-113)/125 = 24/125 ≈ 19.2%
        var prediction = MakePrediction(predicted: 125m, lower: 113m, upper: 137m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().Be(RecommendationLevel.StronglyRecommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 3. Honesty ceiling: fallback predictor must never be StronglyRecommended
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_FallbackPredictor_CapsAtRecommended_NeverStrongly()
    {
        // Even with huge upside and tight interval, fallback predictor must cap at Recommended.
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        var prediction = MakePrediction(
            predicted: 200m, lower: 190m, upper: 210m, // upside=100%, width=10% → raw = StronglyRecommended
            activePredictor: MlContract.FallbackPredictor);

        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().NotBe(RecommendationLevel.StronglyRecommended,
            "the fallback predictor ceiling must prevent StronglyRecommended");
        result.Data.LowTrust.Should().BeTrue();
    }

    [Fact]
    public async Task Recommend_LowConfidence_CapsAtRecommended()
    {
        // Low confidence (string) caps at Recommended even with big upside.
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        var prediction = MakePrediction(
            predicted: 200m, lower: 190m, upper: 210m,
            confidence: MlContract.LowConfidence); // "Low"

        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.RecommendationLevel.Should().NotBe(RecommendationLevel.StronglyRecommended);
        result.Data.LowTrust.Should().BeTrue();
    }

    [Fact]
    public async Task Recommend_LowConfidence_CaseInsensitive_StillCaps()
    {
        // "LOW" (uppercase) must still trigger the cap (OrdinalIgnoreCase).
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        var prediction = MakePrediction(
            predicted: 200m, lower: 190m, upper: 210m,
            confidence: "LOW"); // intentional case drift

        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.LowTrust.Should().BeTrue();
        result.Data.RecommendationLevel.Should().NotBe(RecommendationLevel.StronglyRecommended);
    }

    [Fact]
    public async Task Recommend_FallbackPredictor_CaseInsensitive_StillCaps()
    {
        // "CROP_MEAN_FALLBACK" (uppercase) must still trigger the cap.
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        var prediction = MakePrediction(
            predicted: 200m, lower: 190m, upper: 210m,
            activePredictor: "CROP_MEAN_FALLBACK");

        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.Data.LowTrust.Should().BeTrue();
        result.Data.RecommendationLevel.Should().NotBe(RecommendationLevel.StronglyRecommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 4. Wide interval (>60%) width cap
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recommend_WideInterval_CapsAtRecommendedWithRiskOrBelow()
    {
        // Upside=25%, but interval width > 60% → cap at RecommendedWithRisk.
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // predicted=125, lower=50, upper=200 → width=150/125=120% >> 60%
        var prediction = MakePrediction(predicted: 125m, lower: 50m, upper: 200m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        ((int)result.Data.RecommendationLevel).Should().BeLessThanOrEqualTo(
            (int)RecommendationLevel.RecommendedWithRisk,
            "width >60% must cap at RecommendedWithRisk or below");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 5. Null prediction = fail-safe (not a crash)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_NullPrediction_ReturnsFailure()
    {
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync((HarvestPredictionDto?)null);

        var result = await handler.Handle(MakeQuery(cropId), default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 6. UpsidePct and IntervalWidthPct are rounded to 1 d.p.
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_OutputPcts_RoundedToOneDecimalPlace()
    {
        var (handler, repMock, clientMock) = BuildHandler();
        var cropId = Guid.NewGuid();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        repMock.Setup(r => r.GetRecentByCropIdAsync(cropId, It.IsAny<int>(), It.IsAny<DateOnly>(), default))
               .ReturnsAsync(PriceList(100m, today));

        // upside = (110 - 100) / 100 = 10.0%
        // width  = (121 - 99) / 110 = 20.0%
        var prediction = MakePrediction(predicted: 110m, lower: 99m, upper: 121m);
        clientMock.Setup(c => c.PredictAsync(cropId, It.IsAny<DateOnly>(), default))
                  .ReturnsAsync(prediction);

        var result = await handler.Handle(MakeQuery(cropId), default);

        // round-tripped through Math.Round(x * 100, 1) 
        (result.Data.UpsidePct % 0.1m).Should().Be(0m, "UpsidePct must be a multiple of 0.1");
        (result.Data.IntervalWidthPct % 0.1m).Should().Be(0m, "IntervalWidthPct must be a multiple of 0.1");
    }
}
