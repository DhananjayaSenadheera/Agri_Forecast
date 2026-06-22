using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Lightweight projection of a distinct external market product.
public record ExternalProduct(int ExternalProductId, string Name);

public interface IMarketPriceRepository
{
    Task AddAsync(MarketPrice marketPrice, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<MarketPrice> marketPrices, CancellationToken ct = default);
    Task<bool> ExistsAsync(string source, int externalProductId, DateOnly priceDate, CancellationToken ct = default);
    Task<HashSet<DateOnly>> GetExistingDatesAsync(string source, int externalProductId, CancellationToken ct = default);

    // Links existing rows (CropId == null) for a source+product to a crop. Returns rows updated.
    Task<int> BackfillCropIdAsync(string source, int externalProductId, Guid cropId, CancellationToken ct = default);
    Task<List<MarketPrice>> GetByCropIdAsync(Guid cropId, DateOnly from, CancellationToken ct = default);

    // Distinct external products (id + most recent name) already present for a source.
    // Used to auto-provision a crop per product when healing historic data.
    Task<List<ExternalProduct>> GetDistinctExternalProductsAsync(string source, CancellationToken ct = default);
}