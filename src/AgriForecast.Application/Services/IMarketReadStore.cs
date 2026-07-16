using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over Markets for GET /api/markets/get/all. Thin DB seam so
// GetMarketsQueryHandler is unit-testable with canned rows (mirrors IMarketOverviewStore).
// The hasPricesOnly filter is applied in the store (an EXISTS over confirmed
// PriceObservations) rather than the handler, so the row set the handler maps is already
// the intended subset.
public interface IMarketReadStore
{
    // All markets, ordered by name. When hasPricesOnly is true, only markets with at
    // least one CONFIRMED PriceObservation (IsUnitConfirmed=1) are returned.
    Task<IReadOnlyList<MarketListRow>> GetMarketsAsync(
        bool hasPricesOnly, CancellationToken ct = default);
}

// One Markets row flattened for the list DTO. MarketType is the Domain enum (serialized
// as an int by the API, no JsonStringEnumConverter).
public sealed record MarketListRow(
    Guid Id,
    string Name,
    string? District,
    MarketType MarketType,
    bool IsEconomicCenter);
