namespace AgriForecast.Application.Services;

// Read-only projection over PriceObservations for GET /api/prices/crop/{cropId}/history.
// Thin DB seam so GetPriceHistoryQueryHandler is unit-testable with canned rows (mirrors
// IMarketOverviewStore). The store ALWAYS fails closed: only CONFIRMED rows
// (IsUnitConfirmed=1) with a usable Min AND Max (> 0) are returned — quarantined /
// partial rows never reach the handler, so the daily envelope is never fabricated.
public interface IPriceHistoryStore
{
    // Latest ObservedDate among usable rows for the (crop, optional market) series, or
    // null when the series has no usable data. This is the window anchor.
    Task<DateOnly?> GetLatestObservedDateAsync(
        Guid cropId, Guid? marketId, CancellationToken ct = default);

    // Usable rows in [fromInclusive, toInclusive] for the (crop, optional market) series.
    // One row per source observation (a date can carry several: multiple markets and/or
    // sources); the handler aggregates to one point per date.
    Task<IReadOnlyList<PriceHistoryRow>> GetRowsAsync(
        Guid cropId, Guid? marketId, DateOnly fromInclusive, DateOnly toInclusive,
        CancellationToken ct = default);
}

// One confirmed, usable PriceObservations row (MinPrice/MaxPrice both > 0, guaranteed by
// the store's filter).
public sealed record PriceHistoryRow(DateOnly Date, decimal MinPrice, decimal MaxPrice);
