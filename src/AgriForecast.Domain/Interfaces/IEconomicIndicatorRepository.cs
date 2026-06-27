using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IEconomicIndicatorRepository
{
    Task AddAsync(EconomicIndicator indicator, CancellationToken ct = default);
    Task<bool> ExistsAsync(DateTime date, string indicatorCode, CancellationToken ct = default);
    Task<List<EconomicIndicator>> GetRangeAsync(DateTime from, DateTime to, string indicatorCode, CancellationToken ct = default);
}
