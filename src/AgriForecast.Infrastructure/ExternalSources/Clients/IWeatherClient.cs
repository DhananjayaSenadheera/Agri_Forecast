using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Abstracts the weather provider (Open-Meteo, OpenWeather, ...).
public interface IWeatherClient
{
    // Daily temperature + precipitation for a location across an inclusive date range.
    Task<IReadOnlyList<DailyWeather>> GetDailyAsync(double lat, double lon, DateOnly from, DateOnly to, CancellationToken ct);
}
