using AgriForecast.Application.Services;
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
                m.IsEconomicCenter))
            .ToListAsync(ct);
    }
}
