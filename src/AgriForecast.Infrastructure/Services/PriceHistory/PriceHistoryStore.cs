using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.PriceHistory;

// Read-only projection over PriceObservations for the price-history endpoint. PriceObservations is the
// multi-market table that supersedes the Dambulla-only MarketPrices; ObservedDate is the series' date axis.
//
// Fail-closed, and repeated deliberately in both queries so neither can drift: a row feeds the series only
// when IsUnitConfirmed = 1 (the unified hold flag — unit-unproven or Python-flagged outlier) and both
// MinPrice and MaxPrice are > 0 (a usable low-high band; a null or zero bound cannot form an honest range).
// SQL treats null > 0 as false, so the > 0 predicates also exclude null bounds.
public class PriceHistoryStore : IPriceHistoryStore
{
    private readonly AgriForecastDbContext _db;

    public PriceHistoryStore(AgriForecastDbContext db) => _db = db;

    public async Task<DateOnly?> GetLatestObservedDateAsync(
        Guid cropId, Guid? marketId, CancellationToken ct = default)
    {
        var usable = UsableRows(cropId, marketId);
        if (!await usable.AnyAsync(ct)) return null;
        return await usable.MaxAsync(po => po.ObservedDate, ct);
    }

    public async Task<IReadOnlyList<PriceHistoryRow>> GetRowsAsync(
        Guid cropId, Guid? marketId, DateOnly fromInclusive, DateOnly toInclusive,
        CancellationToken ct = default)
    {
        return await UsableRows(cropId, marketId)
            .Where(po => po.ObservedDate >= fromInclusive && po.ObservedDate <= toInclusive)
            .Select(po => new PriceHistoryRow(po.ObservedDate, po.MinPrice!.Value, po.MaxPrice!.Value))
            .ToListAsync(ct);
    }

    // The single fail-closed predicate shared by both queries.
    private IQueryable<Domain.Entities.PriceObservation> UsableRows(Guid cropId, Guid? marketId)
    {
        var query = _db.PriceObservations.AsNoTracking().Where(po =>
            po.IsUnitConfirmed
            && po.CropId == cropId
            && po.MinPrice > 0m
            && po.MaxPrice > 0m);

        if (marketId.HasValue)
            query = query.Where(po => po.MarketId == marketId.Value);
        else
            // Cross-market envelope: NationalAggregate is a statistical series, not a location, so it must
            // never widen a location-level band.
            query = query.Where(po => _db.Markets.Any(m =>
                m.Id == po.MarketId && m.MarketType != Domain.Enums.MarketType.NationalAggregate));

        return query;
    }
}
