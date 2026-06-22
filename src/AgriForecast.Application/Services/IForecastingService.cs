using AgriForecast.Application.Requests.Forecast.DTOs;

namespace AgriForecast.Application.Services;

public interface IForecastingService
{
    Task<List<MonthlyForecast_GetDto>> GetForecastHistoryAsync(Guid cropId, int months, CancellationToken ct = default);
}
