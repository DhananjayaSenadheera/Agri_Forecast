using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Abstracts the economic-data provider (open.er-api.com, ...).
public interface IEconomicDataClient
{
    // Latest USD -> LKR rate. Returns null if the provider is unavailable or the response is unusable.
    Task<FxRate?> GetLatestUsdToLkrAsync(CancellationToken ct);
}
