using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services.Forecasting;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for ForecastingService — the pure logic portions
/// (trend calculation, confidence assignment, grouping/averaging).
/// All repository calls are mocked.
/// </summary>
public class ForecastingServiceTests
{
    private static (ForecastingService svc,
                    Mock<IMarketPriceRepository> priceMock,
                    Mock<ICropRepository> cropMock)
        Build()
    {
        var priceMock = new Mock<IMarketPriceRepository>();
        var cropMock = new Mock<ICropRepository>();
        var logger = Mock.Of<ILogger<ForecastingService>>();
        var svc = new ForecastingService(priceMock.Object, cropMock.Object, logger);
        return (svc, priceMock, cropMock);
    }

    // Crop.Id has a private setter; use the factory which calls Guid.NewGuid() internally.
    // We capture the Id from the created Crop so we can key mocks on it.
    private static Crop MakeCrop(string name = "Tomato")
        => Crop.CreateFromExternalSource(name, source: "test", cropCode: name[..3].ToUpper());

    private static MarketPrice MakePrice(Guid cropId, int year, int month, int day, decimal min, decimal max) =>
        new()
        {
            Id = Guid.NewGuid(),
            CropId = (Guid?)cropId,     // Guid? — nullable in domain entity
            MinPrice = min,
            MaxPrice = max,
            PriceDate = new DateOnly(year, month, day),
            Source = "test"
        };

    // 1. No prices -> empty list returned.

    [Fact]
    public async Task GetForecastHistory_NoPrices_ReturnsEmpty()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);
        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(new List<MarketPrice>());

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result.Should().BeEmpty();
    }

    // 2. Unknown crop -> empty list, no NullReferenceException.

    [Fact]
    public async Task GetForecastHistory_UnknownCrop_ReturnsEmpty()
    {
        var (svc, priceMock, cropMock) = Build();
        var cropId = Guid.NewGuid();

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync((Crop?)null);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result.Should().BeEmpty();
    }

    // 3. AveragePrice = (min + max) / 2 averaged across the month's rows.

    [Fact]
    public async Task GetForecastHistory_AveragePrice_IsCorrect()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        // Two rows for Jan 2026: mid = (100+200)/2=150, (120+180)/2=150 → avg = 150
        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 5,  100m, 200m),
            MakePrice(cropId, 2026, 1, 15, 120m, 180m)
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result.Should().HaveCount(1);
        result[0].AveragePrice.Should().Be(150m);
    }

    // 4. Trend: second half more than 5% above the first half -> Up.

    [Fact]
    public async Task GetForecastHistory_Trend_SecondHalfHigher_ReturnsUp()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        // 4 months: first 2 avg = 100, last 2 avg = 120 → change = +20%
        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 1,  95m,  105m), // mid=100
            MakePrice(cropId, 2026, 2, 1,  95m,  105m), // mid=100
            MakePrice(cropId, 2026, 3, 1,  115m, 125m), // mid=120
            MakePrice(cropId, 2026, 4, 1,  115m, 125m), // mid=120
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result.Should().NotBeEmpty();
        result[0].Trend.Should().Be(PriceTrend.Up);
    }

    // 5. Trend: second half more than 5% below the first half -> Down.

    [Fact]
    public async Task GetForecastHistory_Trend_SecondHalfLower_ReturnsDown()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 1,  115m, 125m), // mid=120
            MakePrice(cropId, 2026, 2, 1,  115m, 125m), // mid=120
            MakePrice(cropId, 2026, 3, 1,  95m,  105m), // mid=100
            MakePrice(cropId, 2026, 4, 1,  95m,  105m), // mid=100
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result[0].Trend.Should().Be(PriceTrend.Down);
    }

    // 6. Trend: within 5% either way -> Stable.

    [Fact]
    public async Task GetForecastHistory_Trend_Flat_ReturnsStable()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 1, 100m, 100m), // mid=100
            MakePrice(cropId, 2026, 2, 1, 102m, 102m), // mid=102 → change=+2%, within ±5%
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result[0].Trend.Should().Be(PriceTrend.Stable);
    }

    // 7. Confidence: fewer than 10 data points -> Low.

    [Fact]
    public async Task GetForecastHistory_FewDataPoints_ConfidenceLow()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        // 3 rows total → DataPoints = 3 < 10 → Low
        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 1,  100m, 100m),
            MakePrice(cropId, 2026, 1, 5,  100m, 100m),
            MakePrice(cropId, 2026, 1, 10, 100m, 100m),
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result[0].Confidence.Should().Be(ForecastConfidence.Low);
    }

    // 8. Confidence: 10-29 data points -> Medium.

    [Fact]
    public async Task GetForecastHistory_10to29DataPoints_ConfidenceMedium()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        // 15 rows spread across two months → total DataPoints = 15 → Medium
        var prices = new List<MarketPrice>();
        for (int i = 1; i <= 15; i++)
        {
            // days 1-10 in Jan, days 11-15 in Feb (day offset)
            int month = i <= 10 ? 1 : 2;
            int day = i <= 10 ? i : i - 10;
            prices.Add(MakePrice(cropId, 2026, month, day, 100m, 100m));
        }

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        // Total DataPoints across all months = 15 → Medium
        var totalPoints = result.Sum(r => r.DataPoints);
        totalPoints.Should().Be(15);
        result[0].Confidence.Should().Be(ForecastConfidence.Medium);
    }

    // 9. Confidence: 30 or more data points -> High.

    [Fact]
    public async Task GetForecastHistory_30PlusDataPoints_ConfidenceHigh()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        // 31 rows in one month → total = 31 ≥ 30 → High
        var prices = Enumerable.Range(1, 28).Concat(new[] {28,28,28})
            .Select((_, i) => MakePrice(cropId, 2026, 1, (i % 28) + 1, 100m, 100m))
            .Take(31)
            .ToList();

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result[0].Confidence.Should().Be(ForecastConfidence.High);
    }

    // 10. Multiple months are grouped correctly, one DTO per calendar month.

    [Fact]
    public async Task GetForecastHistory_TwoMonths_ReturnsTwoDtos()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        var prices = new List<MarketPrice>
        {
            MakePrice(cropId, 2026, 1, 10, 100m, 100m),
            MakePrice(cropId, 2026, 2, 10, 110m, 110m),
        };

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(prices);

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result.Should().HaveCount(2, "one entry per calendar month");
        result.Select(r => r.Month.Month).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    // 11. A single price row -> Stable trend (not enough monthly data for a slope).

    [Fact]
    public async Task GetForecastHistory_OneMonth_TrendIsStable()
    {
        var (svc, priceMock, cropMock) = Build();
        var crop = MakeCrop();
        var cropId = crop.Id;

        cropMock.Setup(c => c.GetByIdAsync(cropId)).ReturnsAsync(crop);

        priceMock.Setup(p => p.GetByCropIdAsync(cropId, It.IsAny<DateOnly>(), default))
                 .ReturnsAsync(new List<MarketPrice>
                 {
                     MakePrice(cropId, 2026, 1, 1, 100m, 100m)
                 });

        var result = await svc.GetForecastHistoryAsync(cropId, 12);

        result[0].Trend.Should().Be(PriceTrend.Stable,
            "a single monthly average cannot produce a slope");
    }
}
