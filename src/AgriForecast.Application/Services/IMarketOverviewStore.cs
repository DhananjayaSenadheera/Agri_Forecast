namespace AgriForecast.Application.Services;

// Read-only projection of confirmed price observations for GetMarketOverviewQueryHandler. The handler owns
// all business logic; this is a thin DB seam so it can be unit-tested with canned rows.
public interface IMarketOverviewStore
{
    // Latest CONFIRMED observation date (the response asOf). Null when no confirmed data.
    Task<DateOnly?> GetLatestObservationDateAsync(CancellationToken ct = default);

    // Confirmed rows in [fromInclusive, toInclusive], joined to crop and market names. Quarantined rows
    // (IsUnitConfirmed=0) and rows without a resolvable crop or market are excluded by the store.
    Task<IReadOnlyList<MarketPriceWindowRow>> GetRowsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);
}

// One confirmed observation flattened with its crop and market names. Nullable price columns are coalesced
// to 0 by the store, where 0 means "absent" to the handler's precedence rules.
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
