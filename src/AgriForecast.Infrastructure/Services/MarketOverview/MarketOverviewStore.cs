using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.MarketOverview;

// Read-only projection over PriceObservations for the market-overview endpoint.
// PriceObservations is the multi-market table (10 markets, ~363K rows) that supersedes
// the Dambulla-only MarketPrices. MarketId is a real Markets FK, so market names resolve
// directly. ObservedDate is the economic event date (the overview's date axis).
//
// QUARANTINE: rows with IsUnitConfirmed=0 are HELD and must never feed aggregation.
// That single bit is the unified hold flag: it is set to 0 both for unit-unproven rows
// (ingestion default) AND for rolling-IQR outliers held by the Python
// data_quality.flag_price_outliers() machinery. Filtering IsUnitConfirmed=1 fail-closed
// excludes both classes; there is no separate outlier column on the table.
public class MarketOverviewStore : IMarketOverviewStore
{
    private readonly AgriForecastDbContext _db;

    public MarketOverviewStore(AgriForecastDbContext db) => _db = db;

    public async Task<DateOnly?> GetLatestObservationDateAsync(CancellationToken ct = default)
    {
        // Latest date among CONFIRMED rows only, so asOf reflects data we actually serve.
        var confirmed = _db.PriceObservations.Where(po => po.IsUnitConfirmed);
        if (!await confirmed.AnyAsync(ct)) return null;
        return await confirmed.MaxAsync(po => po.ObservedDate, ct);
    }

    public async Task<IReadOnlyList<MarketPriceWindowRow>> GetRowsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
    {
        // Window filter + confirmed-only filter applied server-side; only the columns the
        // handler needs are projected. Nullable prices are coalesced to 0 (== "absent").
        var rows = await (
            from po in _db.PriceObservations
            where po.IsUnitConfirmed
                  && po.ObservedDate >= fromInclusive && po.ObservedDate <= toInclusive
                  && po.CropId != null
            join c in _db.Crops on po.CropId equals c.Id
            join m in _db.Markets on po.MarketId equals m.Id
            select new MarketPriceWindowRow(
                c.Id,
                c.Name,
                m.Id,
                m.Name,
                po.ObservedDate,
                po.MinPrice ?? 0m,
                po.MaxPrice ?? 0m,
                po.WholesalePrice ?? 0m,
                po.RetailPrice ?? 0m))
            .ToListAsync(ct);

        return rows;
    }
}
