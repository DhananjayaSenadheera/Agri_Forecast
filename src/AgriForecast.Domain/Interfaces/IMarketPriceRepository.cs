using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IMarketPriceRepository
{
    Task AddAsync(MarketPrice marketPrice, CancellationToken ct = default);
    Task AddRangeAsync(IEnumerable<MarketPrice> marketPrices, CancellationToken ct = default);
    Task<bool> ExistsAsync(string source, int externalProductId, DateOnly priceDate, CancellationToken ct = default);
    Task<HashSet<DateOnly>> GetExistingDatesAsync(string source, int externalProductId, CancellationToken ct = default);

    // Links existing rows (CropId == null) for a source+product to a crop. Returns rows updated.
    Task<int> BackfillCropIdAsync(string source, int externalProductId, Guid cropId, CancellationToken ct = default);
    
}