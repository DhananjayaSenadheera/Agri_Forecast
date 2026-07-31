using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.PortfolioRead;

// Read-only projections behind the farmer portfolio. Pure EF LINQ, AsNoTracking — the price precedence,
// the trend, the market fallback and the date formatting all live in the handlers.
//
// The price reads repeat the same fail-closed filter the price-history and market-overview stores use
// (IsUnitConfirmed = 1), deliberately written out here rather than shared across stores, so a change to
// one endpoint's definition of "usable" can never silently move another's.
public class PortfolioReadStore : IPortfolioReadStore
{
    private readonly AgriForecastDbContext _db;

    public PortfolioReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(
        Guid userId, CancellationToken ct = default)
    {
        // The watched markets are a correlated collection subquery joined to Markets for the display
        // fields. Ordered oldest-chosen first (CreatedAtUtc, then MarketId as a deterministic tiebreak for
        // two markets added in the same request): this IS the wire order of the dashboard's market blocks,
        // and markets[0] is the tab the farmer opens on. An unordered projection would let the query plan
        // decide which market a farmer sees first.
        return await _db.UserCropWatchlists.AsNoTracking()
            .Where(w => w.UserId == userId)
            .Join(
                _db.Crops,
                w => w.CropId,
                c => c.Id,
                (w, c) => new { Watch = w, Crop = c })
            .OrderBy(x => x.Crop.Name)
            .ThenBy(x => x.Watch.CropId)
            .Select(x => new WatchlistRow(
                x.Watch.CropId,
                x.Crop.Name,
                x.Crop.CropCode,
                x.Watch.PlantedDate,
                _db.UserCropWatchMarkets
                    .Where(wm => wm.UserCropWatchlistId == x.Watch.Id)
                    .Join(
                        _db.Markets,
                        wm => wm.MarketId,
                        m => m.Id,
                        (wm, m) => new { Link = wm, Market = m })
                    .OrderBy(z => z.Link.CreatedAtUtc)
                    .ThenBy(z => z.Link.MarketId)
                    .Select(z => new WatchlistMarketRow(z.Market.Id, z.Market.Name, z.Market.ShortCode))
                    .ToList(),
                x.Watch.CreatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CropLatestObservation>> GetLatestObservedDatesAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
    {
        if (cropIds.Count == 0) return Array.Empty<CropLatestObservation>();

        // One grouped MAX per crop, never a single MAX over the whole set — a crop's own freshest date is
        // the only honest anchor for its own window.
        var grouped = await UsableRows(cropIds, marketId)
            .GroupBy(po => po.CropId)
            .Select(g => new { CropId = g.Key, Latest = g.Max(po => po.ObservedDate) })
            .ToListAsync(ct);

        // The null-CropId rows are already excluded by UsableRows; the HasValue filter is only what the
        // nullable projection needs to unwrap the key.
        return grouped
            .Where(g => g.CropId.HasValue)
            .Select(g => new CropLatestObservation(g.CropId!.Value, g.Latest))
            .ToList();
    }

    public async Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
        IReadOnlyCollection<CropObservationWindow> windows, Guid marketId,
        CancellationToken ct = default)
    {
        if (windows.Count == 0) return Array.Empty<PortfolioObservationRow>();

        // One query per DISTINCT window start, not one per crop: every crop quoted on the same day shares a
        // window, so a watchlist at a live market is a single round trip. Grouping this way also keeps each
        // read bounded by its own window instead of widening every crop's scan back to the stalest crop's
        // start date, which is what a single min(from) would do.
        var rows = new List<PortfolioObservationRow>();

        foreach (var window in windows.GroupBy(w => w.FromInclusive))
        {
            var ids = window.Select(w => w.CropId).Distinct().ToArray();
            var fromInclusive = window.Key;

            rows.AddRange(await UsableRows(ids, marketId)
                .Where(po => po.ObservedDate >= fromInclusive)
                .Select(po => new PortfolioObservationRow(
                    po.CropId!.Value,
                    po.ObservedDate,
                    po.MinPrice ?? 0m,
                    po.MaxPrice ?? 0m,
                    po.WholesalePrice ?? 0m,
                    po.RetailPrice ?? 0m))
                .ToListAsync(ct));
        }

        return rows;
    }

    public async Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
        IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default)
    {
        if (cropIds.Count == 0) return Array.Empty<PortfolioSnapshotRow>();

        var ids = cropIds.ToArray();

        // Newest snapshot per crop, expressed as MAX(SnapshotDate) per crop joined back to the row. The
        // (CropId, SnapshotDate) UNIQUE index guarantees that join matches exactly one row per crop, so no
        // tiebreak is needed and no crop can come back twice.
        var latest = _db.ForecastSnapshots.AsNoTracking()
            .Where(s => ids.Contains(s.CropId))
            .GroupBy(s => s.CropId)
            .Select(g => new { CropId = g.Key, SnapshotDate = g.Max(s => s.SnapshotDate) });

        return await latest
            .Join(
                _db.ForecastSnapshots.AsNoTracking(),
                k => new { k.CropId, k.SnapshotDate },
                s => new { s.CropId, s.SnapshotDate },
                (k, s) => s)
            // Frozen prediction columns only — the actual/error columns are never projected here.
            .Select(s => new PortfolioSnapshotRow(
                s.CropId,
                s.SnapshotDate,
                s.HarvestDate,
                s.PredictedPrice,
                s.LowerBound,
                s.UpperBound,
                s.Confidence,
                s.ActivePredictor,
                s.ModelVersion))
            .ToListAsync(ct);
    }

    public async Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default)
    {
        return await _db.Markets.AsNoTracking()
            .Where(m => m.Id == marketId)
            .Select(m => new PortfolioMarketRow(m.Id, m.Name, m.ShortCode, m.IsEconomicCenter))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default)
    {
        // Deliberately NOT filtered on IsActive: the economic centre is the structural national price
        // anchor, so deactivating it in the admin UI must not silently strip a crop with no chosen market
        // of the only block it would have. Ordered by MarketCode so the answer is deterministic if a
        // second market is ever flagged; today exactly one row (Dambulla, MKT00000001) carries the flag.
        return await _db.Markets.AsNoTracking()
            .Where(m => m.IsEconomicCenter)
            .OrderBy(m => m.MarketCode)
            .Select(m => new PortfolioMarketRow(m.Id, m.Name, m.ShortCode, m.IsEconomicCenter))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default)
        => await _db.Crops.AsNoTracking().AnyAsync(c => c.Id == cropId, ct);

    // THE SALES LOG. Every query below opens with the same UserId predicate — that filter is the whole of
    // the isolation story for the most private table in the product, so it is written first and never
    // parameterised away into a shared helper that a future edit could call without it.
    //
    // Crops is an inner join (a sale always has one, FK-enforced) and Markets a LEFT join (the market is
    // optional), written as GroupJoin/DefaultIfEmpty because that is the only spelling EF translates to a
    // real LEFT JOIN. No navigation properties are used or needed: Crop and Market have none into this
    // table by design (PRD 3.1), so nothing can Include its way from reference data into a farmer's sales.

    public async Task<UserSalesPage> GetSalesPageAsync(
        Guid userId, Guid? cropId, int page, int pageSize, CancellationToken ct = default)
    {
        var filtered = _db.UserSales.AsNoTracking()
            .Where(s => s.UserId == userId);

        // An unknown crop id is a filter that matches nothing, not an error — see GetSalesQuery.
        if (cropId.HasValue)
            filtered = filtered.Where(s => s.CropId == cropId.Value);

        // Counted BEFORE paging and AFTER the crop filter, so the UI's page count describes what it asked
        // for. Counted on the same IQueryable rather than a second hand-written predicate, so the two can
        // never disagree about what is being paged.
        var total = await filtered.CountAsync(ct);

        // ORDER AND PAGE THE ENTITY QUERY, THEN PROJECT — never the other way round. Sorting the projected
        // record made EF try to translate an ORDER BY over a constructor call and it refused the whole
        // query (a runtime 500 on the first GET). Ordering here is over real columns of one table, which is
        // also what lets IX_UserSales_UserSaleDate serve the page.
        //
        // Newest sale first. CreatedAtUtc breaks ties between two sales on the SAME day (the row typed
        // second is shown first), and Id is the last resort purely so the order is TOTAL: without a
        // deterministic tiebreak, two rows sharing a date AND an instant could swap places between two page
        // requests and be shown twice or not at all.
        var items = await Project(
                filtered
                    .OrderByDescending(s => s.SaleDate)
                    .ThenByDescending(s => s.CreatedAtUtc)
                    .ThenByDescending(s => s.Id)
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize))
            .ToListAsync(ct);

        return new UserSalesPage(items, total);
    }

    public async Task<UserSaleRow?> GetSaleAsync(
        Guid userId, Guid saleId, CancellationToken ct = default)
    {
        // BOTH predicates, so a row belonging to somebody else comes back null exactly like one that does
        // not exist — the caller cannot tell them apart and answers the same 404 to both.
        return await Project(_db.UserSales.AsNoTracking()
                .Where(s => s.UserId == userId && s.Id == saleId))
            .FirstOrDefaultAsync(ct);
    }

    // The ONE projection both sales reads use, so the list and the read-back cannot render a row
    // differently. Takes an ALREADY user-scoped (and, for the list, already ordered and paged) query: it
    // adds no filter and no ordering of its own, and must never be handed an unscoped one.
    //
    // The crop and market display fields come from CORRELATED SUBQUERIES rather than joins, matching
    // GetWatchlistAsync above. Two reasons, both learned the hard way against the real database:
    //   * a join changes the shape EF composes over, which broke the paged query's ORDER BY;
    //   * the market is OPTIONAL, and a GroupJoin needs matching key types — casting Guid to Guid? to line
    //     them up produced an object comparison EF could not translate at all.
    // A subquery per column is honest here: both tables are tiny reference data, both lookups are by
    // primary key, and neither can change the row count the way a mis-written join can.
    private IQueryable<UserSaleRow> Project(IQueryable<Domain.Entities.UserSale> scoped)
    {
        return scoped.Select(s => new UserSaleRow(
            s.Id,
            s.CropId,
            _db.Crops.Where(c => c.Id == s.CropId).Select(c => c.Name).FirstOrDefault()!,
            _db.Crops.Where(c => c.Id == s.CropId).Select(c => c.CropCode).FirstOrDefault(),
            s.MarketId,
            // Null when the sale names no market, and null again if that market has somehow gone — the FK
            // restricts the delete, so the second case cannot happen without a manual DBA edit.
            _db.Markets.Where(m => m.Id == s.MarketId).Select(m => m.Name).FirstOrDefault(),
            _db.Markets.Where(m => m.Id == s.MarketId).Select(m => m.ShortCode).FirstOrDefault(),
            s.SaleDate,
            s.PricePerKg,
            s.QuantityKg,
            s.Note,
            s.CreatedAtUtc,
            s.UpdatedAtUtc));
    }

    // The single fail-closed predicate shared by the two price queries in this store.
    //
    // IsUnitConfirmed = 1 is the unified hold flag: it excludes both unit-unproven rows and rows the
    // Python data-quality machinery has quarantined as outliers.
    //
    // THE PRICE PREDICATE IS THE EXACT NEGATION OF ObservedUnitPrice.From RETURNING NULL, and it has to
    // be. A row can be unit-confirmed and carry no quote at all (a commodity listed but not traded that
    // day); the handler skips such rows because From gives it nothing to show. If the store still counted
    // them, one of them could be the LATEST row at a market and would win the anchor — the trend window
    // would then be cut from a day with no price, the real (older) price could fall outside it, and the
    // block would report no_recent_price for a market that demonstrably has a price. Store and handler
    // must agree on "has a price" or the anchor and the data disagree.
    //
    // Deliberately NOT the market-overview spelling (Min > 0 && Max > 0): the dashboard serves
    // wholesale-only and retail-only rows through From's precedence, so requiring a full band here would
    // silently drop prices the handler is perfectly able to render.
    private IQueryable<Domain.Entities.PriceObservation> UsableRows(
        IReadOnlyCollection<Guid> cropIds, Guid marketId)
    {
        var ids = cropIds.ToArray();

        return _db.PriceObservations.AsNoTracking().Where(po =>
            po.IsUnitConfirmed
            && po.MarketId == marketId
            && po.CropId != null
            && ids.Contains(po.CropId.Value)
            && (po.MinPrice > 0m || po.MaxPrice > 0m
                || po.WholesalePrice > 0m || po.RetailPrice > 0m));
    }
}
