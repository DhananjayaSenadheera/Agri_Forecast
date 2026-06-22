using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IWeatherRecordRepository
{
    Task AddAsync(WeatherRecord record, CancellationToken ct = default);
    Task<WeatherRecord?> GetByMonthAsync(DateTime month, CancellationToken ct = default);
    Task<List<WeatherRecord>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default);
}
