using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over Markets for GET /api/markets/get/all. The hasPricesOnly filter is applied in
// the store (an EXISTS over confirmed PriceObservations), so the handler maps an already-correct row set.
public interface IMarketReadStore
{
    // All markets ordered by name. With hasPricesOnly, only those with a confirmed PriceObservation.
    Task<IReadOnlyList<MarketListRow>> GetMarketsAsync(
        bool hasPricesOnly, CancellationToken ct = default);
}

// One Markets row flattened for the list DTO. MarketType is the domain enum, serialized as an int.
// The last three fields power the admin monitoring view:
//   HasStoredData    — the market has at least one PriceObservation of any status (is it ingesting at all).
//   LastStoredDate   — MAX(ObservedDate) over those rows; null when none. Serializes as yyyy-MM-dd.
//   IsTrainingSource — feature-safe (not a NationalAggregate pseudo-market, not an ECOMAP twin) AND
//                      carrying at least one usable observation. Mirrors the Python
//                      get_feature_safe_market_ids gate so the two never disagree about what trains.
public sealed record MarketListRow(
    Guid Id,
    string Name,
    // Short display code (e.g. "DEC"); empty when unassigned. Display-only, never a key.
    string ShortCode,
    string? District,
    MarketType MarketType,
    bool IsEconomicCenter,
    bool HasStoredData,
    DateOnly? LastStoredDate,
    bool IsTrainingSource);
