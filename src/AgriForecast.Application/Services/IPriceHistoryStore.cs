namespace AgriForecast.Application.Services;

// Read-only projection over PriceObservations for the crop price-history endpoint. Fails closed: only
// confirmed rows (IsUnitConfirmed=1) with a usable Min AND Max (> 0) are returned, so the daily envelope
// is never fabricated.
public interface IPriceHistoryStore
{
    // Latest ObservedDate among usable rows for the series, or null. This is the window anchor.
    Task<DateOnly?> GetLatestObservedDateAsync(
        Guid cropId, Guid? marketId, CancellationToken ct = default);

    // Usable rows in the window. A date can carry several rows (multiple markets or sources); the handler
    // aggregates them to one point per date.
    Task<IReadOnlyList<PriceHistoryRow>> GetRowsAsync(
        Guid cropId, Guid? marketId, DateOnly fromInclusive, DateOnly toInclusive,
        CancellationToken ct = default);
}

// One confirmed, usable row: MinPrice and MaxPrice are both > 0, guaranteed by the store's filter.
public sealed record PriceHistoryRow(DateOnly Date, decimal MinPrice, decimal MaxPrice);
