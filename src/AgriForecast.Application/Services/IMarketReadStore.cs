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
//
// The last three fields power the admin monitoring view (which markets store data, how
// fresh it is, and which ones actually feed model training):
//   HasStoredData    — the market has >=1 PriceObservation of ANY status (literal storage,
//                      NOT gated on the usable predicate — answers "is it ingesting at all").
//   LastStoredDate   — MAX(ObservedDate) over those same rows; null when none. DateOnly,
//                      serialized "yyyy-MM-dd", which the FE formatDate() consumes directly.
//   IsTrainingSource — the market currently feeds training: it is FEATURE-SAFE (not a
//                      NationalAggregate pseudo-market, not an ECOMAP twin) AND carries at
//                      least one USABLE observation (the same confirmed+chartable predicate
//                      hasPricesOnly uses). Mirrors the Python get_feature_safe_market_ids
//                      gate in canonical.py so the two never disagree about what trains.
public sealed record MarketListRow(
    Guid Id,
    string Name,
    string? District,
    MarketType MarketType,
    bool IsEconomicCenter,
    bool HasStoredData,
    DateOnly? LastStoredDate,
    bool IsTrainingSource);
