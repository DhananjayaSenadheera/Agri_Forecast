namespace AgriForecast.Application.Services;

// Read-only projection of confirmed price observations used by
// GetMarketOverviewQueryHandler. The handler owns ALL business logic (window clamp,
// price precedence, mover computation); this store is a thin DB seam so the handler is
// unit-testable with canned rows (mirrors how IRecommendationService abstracts the DB).
public interface IMarketOverviewStore
{
    // Latest CONFIRMED observation date (the response asOf). Null when no confirmed data.
    Task<DateOnly?> GetLatestObservationDateAsync(CancellationToken ct = default);

    // Confirmed rows in [fromInclusive, toInclusive], already joined to crop + market
    // names. Held/quarantined rows (IsUnitConfirmed=0: unit-unproven OR outlier-flagged)
    // are excluded by the store; only rows with a resolvable crop and market are returned.
    Task<IReadOnlyList<MarketPriceWindowRow>> GetRowsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);
}

// One (confirmed) PriceObservations row flattened with its crop + market display names.
// MarketId is the real Markets FK on PriceObservations (10 physical/pseudo markets).
// Nullable price columns are coalesced to 0 by the store (0 == "absent" to the handler's
// precedence rules), so the handler stays free of null-price branching.
public sealed record MarketPriceWindowRow(
    Guid CropId,
    string CropName,
    Guid MarketId,
    string MarketName,
    DateOnly Date,
    decimal MinPrice,
    decimal MaxPrice,
    decimal WholesalePrice,
    decimal RetailPrice);
