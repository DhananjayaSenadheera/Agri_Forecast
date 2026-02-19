using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

public interface IDambullaApiClient
{
    Task<List<DambullaChartItemDto>?> GetProductPriceChartAsync(int productId, CancellationToken ct);
}