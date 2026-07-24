using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.MarketRead;

// Read-only projection over Markets for GET /api/markets/get/all.
//
// The hasPricesOnly filter is a fail-closed EXISTS over USABLE PriceObservations,
// deliberately the SAME predicate the price-history endpoint serves from:
// IsUnitConfirmed=1 (the unified hold flag: unit-unproven OR Python-flagged outlier)
// AND MinPrice/MaxPrice > 0 (a chartable low-high band). Anything looser would list a
// market here whose history endpoint then returns [] — an empty chart the UI offered.
//
// The per-row monitoring fields (HasStoredData / LastStoredDate / IsTrainingSource) are
// correlated subqueries over PriceObservations — the UNIFIED refined layer into which
// Dambulla's MarketPrices are mirrored, so it is the single source of truth for "what is
// stored". They are computed regardless of the hasPricesOnly filter so the admin registry
// (which calls with hasPrices=false) sees every monitored market and its storage status,
// including markets that store nothing yet (e.g. the CBSL national-average placeholder).
public class MarketReadStore : IMarketReadStore
{
    private readonly AgriForecastDbContext _db;

    public MarketReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<IReadOnlyList<MarketListRow>> GetMarketsAsync(
        bool hasPricesOnly, CancellationToken ct = default)
    {
        var query = _db.Markets.AsNoTracking();

        if (hasPricesOnly)
        {
            query = query.Where(m =>
                _db.PriceObservations.Any(po => po.MarketId == m.Id
                    && po.IsUnitConfirmed
                    && po.MinPrice > 0m
                    && po.MaxPrice > 0m));
        }

        return await query
            .OrderBy(m => m.Name)
            .Select(m => new MarketListRow(
                m.Id,
                m.Name,
                m.District,
                m.MarketType,
                m.IsEconomicCenter,
                // Storing anything at all — literal, un-gated (any status counts).
                _db.PriceObservations.Any(po => po.MarketId == m.Id),
                // Freshness: latest observed day of ANY stored row (null when none). Cast to
                // nullable so an empty set yields null instead of throwing on Max().
                _db.PriceObservations
                    .Where(po => po.MarketId == m.Id)
                    .Max(po => (DateOnly?)po.ObservedDate),
                // Feeds training: feature-safe (not an already-averaged NationalAggregate,
                // not a legacy ECOMAP twin) AND carries usable data. Same usable predicate
                // as hasPricesOnly; mirrors canonical.py get_feature_safe_market_ids.
                m.MarketType != MarketType.NationalAggregate
                    && !m.MarketCode.StartsWith("ECOMAP")
                    && _db.PriceObservations.Any(po => po.MarketId == m.Id
                        && po.IsUnitConfirmed
                        && po.MinPrice > 0m
                        && po.MaxPrice > 0m)))
            .ToListAsync(ct);
    }
}
