namespace AgriForecast.Application.Services;

// Read-only projections behind the farmer portfolio (GET /api/portfolio/watchlist and /dashboard). Thin DB
// seam so the dashboard's price precedence, trend and fallback logic are unit-testable without a database;
// pure reads (AsNoTracking).
//
// OWNER SCOPING IS PART OF THE SIGNATURE. GetWatchlistAsync takes the caller's user id and there is no
// overload that returns rows for anyone else, so a handler cannot leak another farmer's crops even by
// mistake. The price and snapshot reads take an explicit crop-id set, which the handler only ever fills
// from the caller's own watchlist.
public interface IPortfolioReadStore
{
    // The caller's watchlist joined to crop display fields and the preferred market's name, ordered by crop
    // name. An empty list is a valid answer (the farmer has not added anything yet), never an error.
    Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(Guid userId, CancellationToken ct = default);

    // Newest usable ObservedDate at one market across the given crops, or null when that market carries no
    // usable observation for any of them. This is the window anchor, mirroring IMarketOverviewStore: the
    // dashboard is anchored on the freshest data that actually exists, never on the wall clock, so it
    // behaves identically whenever it is called.
    Task<DateOnly?> GetLatestObservedDateAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default);

    // Usable observations for those crops at that market on/after fromInclusive. "Usable" is the same
    // fail-closed filter the price-history and market-overview stores apply (IsUnitConfirmed = 1); the
    // nullable price columns are coalesced to 0, where 0 means ABSENT to the handler's precedence rules.
    Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, DateOnly fromInclusive,
        CancellationToken ct = default);

    // The newest ForecastSnapshots row per crop — the FROZEN prediction columns only. The error/actual
    // columns are deliberately not projected: the farmer dashboard shows what was forecast, never how a
    // past forecast scored (that is the admin accuracy surface). Crops with no snapshot are simply absent.
    Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
        IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default);

    // One market's display fields, or null when the id resolves to nothing.
    Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default);

    // The Dedicated Economic Centre — the national price anchor (Dambulla, the only Markets row with
    // IsEconomicCenter = 1). Used both as the default home market for a farmer who has not chosen one and
    // as the fallback when the chosen market has no data for a crop. Null only if no market carries the
    // flag, which the seed and the AddIsEconomicCenterToMarket backfill guarantee it does.
    Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default);

    // Existence check for the validators, so a bad crop id is a structured 400 rather than a raw FK error.
    Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default);
}

// One watchlist row flattened for the list DTO. PreferredMarketName is null exactly when
// PreferredMarketId is null (the FK is Restrict, so a set id always resolves).
//
// UpdatedAtUtc is carried but NOT rendered: it is what HomeMarket.Resolve orders on, so the dashboard can
// answer "which market is this farmer's home market" deterministically from the rows it already has.
public sealed record WatchlistRow(
    Guid CropId,
    string CropName,
    string? CropCode,
    Guid? PreferredMarketId,
    string? PreferredMarketName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

// One usable price observation, projected for the dashboard's latest-price and trend legs. Same
// coalesce-to-0 contract as MarketPriceWindowRow: 0 means the source did not publish that column.
public sealed record PortfolioObservationRow(
    Guid CropId,
    DateOnly Date,
    decimal MinPrice,
    decimal MaxPrice,
    decimal WholesalePrice,
    decimal RetailPrice);

// The frozen prediction columns of the newest snapshot for one crop. No actual/error columns by design.
public sealed record PortfolioSnapshotRow(
    Guid CropId,
    DateOnly SnapshotDate,
    DateOnly? HarvestDate,
    decimal PredictedPrice,
    decimal LowerBound,
    decimal UpperBound,
    string Confidence,
    string ActivePredictor,
    string? ModelVersion);

// A market's display identity. IsEconomicCenter uses the column's own (American) spelling, which is
// already the wire spelling on GET /api/markets/get/all.
public sealed record PortfolioMarketRow(Guid Id, string Name, bool IsEconomicCenter);
