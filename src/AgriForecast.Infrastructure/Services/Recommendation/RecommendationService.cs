using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.Recommendation;

public class RecommendationService : IRecommendationService
{
    private readonly IForecastingService _forecastingService;
    private readonly ICropRepository _cropRepository;
    private readonly ILogger<RecommendationService> _logger;

    public RecommendationService(
        IForecastingService forecastingService,
        ICropRepository cropRepository,
        ILogger<RecommendationService> logger)
    {
        _forecastingService = forecastingService;
        _cropRepository = cropRepository;
        _logger = logger;
    }

    public async Task<List<BestCrop_GetDto>> GetBestCropsAsync(int lookbackMonths, CancellationToken ct = default)
    {
        var crops = await _cropRepository.GetAllAsync();
        var result = new List<BestCrop_GetDto>();

        foreach (var crop in crops)
        {
            var forecasts = await _forecastingService.GetForecastHistoryAsync(crop.Id, lookbackMonths, ct);
            if (!forecasts.Any()) continue;

            var latest = forecasts.Last();
            var level = DetermineRecommendation(latest.Trend, latest.Confidence);

            result.Add(new BestCrop_GetDto
            {
                CropId = crop.Id,
                CropName = crop.Name,
                CropCode = crop.CropCode,
                AveragePrice = latest.AveragePrice,
                Trend = latest.Trend,
                Confidence = latest.Confidence,
                RecommendationLevel = level
            });
        }

        _logger.LogInformation("Generated recommendations for {Count} crops.", result.Count);

        return result
            .OrderByDescending(r => (int)r.RecommendationLevel)
            .ThenByDescending(r => r.AveragePrice)
            .ToList();
    }

    private static RecommendationLevel DetermineRecommendation(PriceTrend trend, ForecastConfidence confidence)
    {
        return (trend, confidence) switch
        {
            (PriceTrend.Up, ForecastConfidence.High)   => RecommendationLevel.StronglyRecommended,
            (PriceTrend.Up, ForecastConfidence.Medium) => RecommendationLevel.Recommended,
            (PriceTrend.Up, ForecastConfidence.Low)    => RecommendationLevel.RecommendedWithRisk,
            (PriceTrend.Stable, _)                     => RecommendationLevel.RecommendedWithRisk,
            (PriceTrend.Down, _)                       => RecommendationLevel.NotRecommended,
            _                                          => RecommendationLevel.NotRecommended
        };
    }
}
