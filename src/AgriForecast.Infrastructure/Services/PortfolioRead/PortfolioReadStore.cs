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
        // The market name is a correlated subquery rather than a left join: PreferredMarketId is nullable,
        // and a subquery yields null for an unset market without any outer-join key-type gymnastics.
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
                x.Watch.PreferredMarketId,
                _db.Markets
                    .Where(m => m.Id == x.Watch.PreferredMarketId)
                    .Select(m => m.Name)
                    .FirstOrDefault(),
                x.Watch.CreatedAtUtc,
                x.Watch.UpdatedAtUtc))
            .ToListAsync(ct);
    }

    public async Task<DateOnly?> GetLatestObservedDateAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
    {
        if (cropIds.Count == 0) return null;

        var usable = UsableRows(cropIds, marketId);
        if (!await usable.AnyAsync(ct)) return null;

        return await usable.MaxAsync(po => po.ObservedDate, ct);
    }

    public async Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, DateOnly fromInclusive,
        CancellationToken ct = default)
    {
        if (cropIds.Count == 0) return Array.Empty<PortfolioObservationRow>();

        return await UsableRows(cropIds, marketId)
            .Where(po => po.ObservedDate >= fromInclusive)
            .Select(po => new PortfolioObservationRow(
                po.CropId!.Value,
                po.ObservedDate,
                po.MinPrice ?? 0m,
                po.MaxPrice ?? 0m,
                po.WholesalePrice ?? 0m,
                po.RetailPrice ?? 0m))
            .ToListAsync(ct);
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
            .Select(m => new PortfolioMarketRow(m.Id, m.Name, m.IsEconomicCenter))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default)
    {
        // Deliberately NOT filtered on IsActive: the economic centre is the structural national price
        // anchor, so deactivating it in the admin UI must not silently strip the dashboard of its default
        // home market and its price fallback. Ordered by MarketCode so the answer is deterministic if a
        // second market is ever flagged; today exactly one row (Dambulla, MKT00000001) carries the flag.
        return await _db.Markets.AsNoTracking()
            .Where(m => m.IsEconomicCenter)
            .OrderBy(m => m.MarketCode)
            .Select(m => new PortfolioMarketRow(m.Id, m.Name, m.IsEconomicCenter))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default)
        => await _db.Crops.AsNoTracking().AnyAsync(c => c.Id == cropId, ct);

    // The single fail-closed predicate shared by the two price queries in this store.
    // IsUnitConfirmed = 1 is the unified hold flag: it excludes both unit-unproven rows and rows the
    // Python data-quality machinery has quarantined as outliers.
    private IQueryable<Domain.Entities.PriceObservation> UsableRows(
        IReadOnlyCollection<Guid> cropIds, Guid marketId)
    {
        var ids = cropIds.ToArray();

        return _db.PriceObservations.AsNoTracking().Where(po =>
            po.IsUnitConfirmed
            && po.MarketId == marketId
            && po.CropId != null
            && ids.Contains(po.CropId.Value));
    }
}
