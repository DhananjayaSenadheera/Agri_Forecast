using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetMonthly;

public class GetMonthlyForecastQueryHandler : IRequestHandler<GetMonthlyForecastQuery, Result<List<MonthlyForecast_GetDto>>>
{
    private readonly IForecastingService _forecastingService;
    private readonly ILogger<GetMonthlyForecastQueryHandler> _logger;

    public GetMonthlyForecastQueryHandler(IForecastingService forecastingService, ILogger<GetMonthlyForecastQueryHandler> logger)
    {
        _forecastingService = forecastingService;
        _logger = logger;
    }

    public async Task<Result<List<MonthlyForecast_GetDto>>> Handle(GetMonthlyForecastQuery request, CancellationToken cancellationToken)
    {
        var forecasts = await _forecastingService.GetForecastHistoryAsync(request.CropId, request.Months, cancellationToken);

        if (forecasts == null || !forecasts.Any())
        {
            _logger.LogInformation("No market price data found for crop {CropId}.", request.CropId);
            return Result<List<MonthlyForecast_GetDto>>.Failure("No price data found for this crop.");
        }

        _logger.LogInformation("Retrieved {Count} monthly forecasts for crop {CropId}.", forecasts.Count, request.CropId);
        return Result<List<MonthlyForecast_GetDto>>.Success(forecasts);
    }
}
