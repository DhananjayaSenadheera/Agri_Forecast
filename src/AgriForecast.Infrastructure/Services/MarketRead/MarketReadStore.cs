using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.MarketRead;

// Read-only projection over Markets for GET /api/markets/get/all.
//
// The hasPricesOnly filter is a fail-closed EXISTS over USABLE PriceObservations, deliberately the same
// predicate the price-history endpoint serves from: IsUnitConfirmed=1 (the unified hold flag) AND
// MinPrice/MaxPrice > 0 (a chartable band). Anything looser would list a market whose history endpoint then
// returns [] — an empty chart the UI had offered.
//
// The per-row monitoring fields (HasStoredData / LastStoredDate / IsTrainingSource) are correlated subqueries
// over PriceObservations, the unified layer into which Dambulla's MarketPrices are mirrored. They are computed
// regardless of hasPricesOnly, so the admin registry sees every monitored market and its storage status,
// including markets that store nothing yet.
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
                m.ShortCode,
                m.District,
                m.MarketType,
                m.IsEconomicCenter,
                // Storing anything at all — literal, un-gated (any status counts).
                _db.PriceObservations.Any(po => po.MarketId == m.Id),
                // Freshness: latest observed day of any stored row (null when none). Cast to nullable so an
                // empty set yields null instead of throwing on Max().
                _db.PriceObservations
                    .Where(po => po.MarketId == m.Id)
                    .Max(po => (DateOnly?)po.ObservedDate),
                // Feeds training: feature-safe (not an already-averaged NationalAggregate, not a legacy ECOMAP
                // twin) AND carrying usable data. Mirrors canonical.py get_feature_safe_market_ids.
                m.MarketType != MarketType.NationalAggregate
                    && !m.MarketCode.StartsWith("ECOMAP")
                    && _db.PriceObservations.Any(po => po.MarketId == m.Id
                        && po.IsUnitConfirmed
                        && po.MinPrice > 0m
                        && po.MaxPrice > 0m)))
            .ToListAsync(ct);
    }
}
