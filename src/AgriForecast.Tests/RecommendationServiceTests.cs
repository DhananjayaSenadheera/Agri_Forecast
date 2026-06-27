using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services.Recommendation;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for RecommendationService.DetermineRecommendation matrix
/// (tested indirectly via GetBestCropsAsync) and ordering/filtering.
/// </summary>
public class RecommendationServiceTests
{
    private static (RecommendationService svc,
                    Mock<IForecastingService> forecastMock,
                    Mock<ICropRepository> cropMock)
        Build()
    {
        var forecastMock = new Mock<IForecastingService>();
        var cropMock = new Mock<ICropRepository>();
        var logger = Mock.Of<ILogger<RecommendationService>>();
        var svc = new RecommendationService(forecastMock.Object, cropMock.Object, logger);
        return (svc, forecastMock, cropMock);
    }

    // Crop.Id has a private setter — use the factory method.
    private static Crop MakeCrop(string name = "Tomato")
        => Crop.CreateFromExternalSource(name, externalProductId: 1, source: "test", cropCode: name.Length >= 3 ? name[..3].ToUpper() : name.ToUpper());

    private static MonthlyForecast_GetDto MakeForecast(Guid cropId, PriceTrend trend, ForecastConfidence confidence) =>
        new()
        {
            CropId = cropId,
            CropName = "Test",
            Month = DateTime.UtcNow,
            AveragePrice = 100m,
            MinPrice = 80m,
            MaxPrice = 120m,
            DataPoints = 10,
            Trend = trend,
            Confidence = confidence
        };

    // ──────────────────────────────────────────────────────────────────────────────
    // 1. Up + High → StronglyRecommended
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetermineRecommendation_UpHigh_IsStronglyRecommended()
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(crop.Id, PriceTrend.Up, ForecastConfidence.High)
                    });

        var result = await svc.GetBestCropsAsync(3);

        result.Should().HaveCount(1);
        result[0].RecommendationLevel.Should().Be(RecommendationLevel.StronglyRecommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 2. Up + Medium → Recommended
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetermineRecommendation_UpMedium_IsRecommended()
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(crop.Id, PriceTrend.Up, ForecastConfidence.Medium)
                    });

        var result = await svc.GetBestCropsAsync(3);

        result[0].RecommendationLevel.Should().Be(RecommendationLevel.Recommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 3. Up + Low → RecommendedWithRisk
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DetermineRecommendation_UpLow_IsRecommendedWithRisk()
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(crop.Id, PriceTrend.Up, ForecastConfidence.Low)
                    });

        var result = await svc.GetBestCropsAsync(3);

        result[0].RecommendationLevel.Should().Be(RecommendationLevel.RecommendedWithRisk);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 4. Stable (any confidence) → RecommendedWithRisk
    // ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForecastConfidence.Low)]
    [InlineData(ForecastConfidence.Medium)]
    [InlineData(ForecastConfidence.High)]
    public async Task DetermineRecommendation_Stable_IsRecommendedWithRisk(ForecastConfidence confidence)
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(crop.Id, PriceTrend.Stable, confidence)
                    });

        var result = await svc.GetBestCropsAsync(3);

        result[0].RecommendationLevel.Should().Be(RecommendationLevel.RecommendedWithRisk);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 5. Down (any confidence) → NotRecommended
    // ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ForecastConfidence.Low)]
    [InlineData(ForecastConfidence.Medium)]
    [InlineData(ForecastConfidence.High)]
    public async Task DetermineRecommendation_Down_IsNotRecommended(ForecastConfidence confidence)
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(crop.Id, PriceTrend.Down, confidence)
                    });

        var result = await svc.GetBestCropsAsync(3);

        result[0].RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 6. Results are ordered by RecommendationLevel descending
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBestCrops_OrderedByRecommendationLevelDescending()
    {
        var (svc, forecastMock, cropMock) = Build();
        var cropA = MakeCrop("CropA");
        var cropB = MakeCrop("CropB");
        var cropC = MakeCrop("CropC");

        cropMock.Setup(c => c.GetAllAsync())
                .ReturnsAsync((IEnumerable<Crop>)new List<Crop> { cropA, cropB, cropC });

        forecastMock.Setup(f => f.GetForecastHistoryAsync(cropA.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(cropA.Id, PriceTrend.Down, ForecastConfidence.High) // NotRecommended
                    });

        forecastMock.Setup(f => f.GetForecastHistoryAsync(cropB.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(cropB.Id, PriceTrend.Up, ForecastConfidence.High) // StronglyRecommended
                    });

        forecastMock.Setup(f => f.GetForecastHistoryAsync(cropC.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>
                    {
                        MakeForecast(cropC.Id, PriceTrend.Stable, ForecastConfidence.High) // RecommendedWithRisk
                    });

        var result = await svc.GetBestCropsAsync(3);

        result.Should().HaveCount(3);
        result[0].RecommendationLevel.Should().Be(RecommendationLevel.StronglyRecommended);
        result[1].RecommendationLevel.Should().Be(RecommendationLevel.RecommendedWithRisk);
        result[2].RecommendationLevel.Should().Be(RecommendationLevel.NotRecommended);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 7. Crops with no forecast history are excluded from results
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBestCrops_EmptyForecast_CropExcluded()
    {
        var (svc, forecastMock, cropMock) = Build();
        var crop = MakeCrop();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop> { crop });
        forecastMock.Setup(f => f.GetForecastHistoryAsync(crop.Id, It.IsAny<int>(), default))
                    .ReturnsAsync(new List<MonthlyForecast_GetDto>()); // empty

        var result = await svc.GetBestCropsAsync(3);

        result.Should().BeEmpty("a crop with no forecast data must not appear in results");
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 8. No crops in DB → empty list (no exception)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBestCrops_NoCrops_ReturnsEmpty()
    {
        var (svc, forecastMock, cropMock) = Build();

        cropMock.Setup(c => c.GetAllAsync()).ReturnsAsync((IEnumerable<Crop>)new List<Crop>());

        var result = await svc.GetBestCropsAsync(3);

        result.Should().BeEmpty();
    }
}
