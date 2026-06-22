using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.Forecasting;

public class ForecastingService : IForecastingService
{
    private readonly IMarketPriceRepository _marketPriceRepository;
    private readonly ICropRepository _cropRepository;
    private readonly ILogger<ForecastingService> _logger;

    public ForecastingService(
        IMarketPriceRepository marketPriceRepository,
        ICropRepository cropRepository,
        ILogger<ForecastingService> logger)
    {
        _marketPriceRepository = marketPriceRepository;
        _cropRepository = cropRepository;
        _logger = logger;
    }

    public async Task<List<MonthlyForecast_GetDto>> GetForecastHistoryAsync(Guid cropId, int months, CancellationToken ct = default)
    {
        var crop = await _cropRepository.GetByIdAsync(cropId);
        if (crop == null)
        {
            _logger.LogWarning("Crop {CropId} not found.", cropId);
            return new List<MonthlyForecast_GetDto>();
        }

        var from = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(-months));
        var prices = await _marketPriceRepository.GetByCropIdAsync(cropId, from, ct);

        if (!prices.Any())
            return new List<MonthlyForecast_GetDto>();

        var grouped = prices
            .GroupBy(p => new DateTime(p.PriceDate.Year, p.PriceDate.Month, 1))
            .OrderBy(g => g.Key)
            .Select(g => new MonthlyForecast_GetDto
            {
                CropId = cropId,
                CropName = crop.Name,
                Month = g.Key,
                AveragePrice = Math.Round(g.Average(p => (p.MinPrice + p.MaxPrice) / 2m), 2),
                MinPrice = g.Min(p => p.MinPrice),
                MaxPrice = g.Max(p => p.MaxPrice),
                DataPoints = g.Count()
            })
            .ToList();

        var trend = CalculateTrend(grouped.Select(m => m.AveragePrice).ToList());
        var totalPoints = grouped.Sum(m => m.DataPoints);
        var confidence = totalPoints switch
        {
            < 10 => ForecastConfidence.Low,
            < 30 => ForecastConfidence.Medium,
            _ => ForecastConfidence.High
        };

        foreach (var m in grouped)
        {
            m.Trend = trend;
            m.Confidence = confidence;
        }

        return grouped;
    }

    private static PriceTrend CalculateTrend(List<decimal> monthlyAverages)
    {
        if (monthlyAverages.Count < 2) return PriceTrend.Stable;

        var mid = monthlyAverages.Count / 2;
        var firstHalf = monthlyAverages.Take(mid).Average();
        var secondHalf = monthlyAverages.Skip(mid).Average();

        if (firstHalf == 0) return PriceTrend.Stable;

        var changePercent = (secondHalf - firstHalf) / firstHalf * 100m;

        return changePercent switch
        {
            > 5m => PriceTrend.Up,
            < -5m => PriceTrend.Down,
            _ => PriceTrend.Stable
        };
    }
}
