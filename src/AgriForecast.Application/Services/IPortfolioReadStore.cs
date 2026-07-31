namespace AgriForecast.Application.Services;

// Read-only projections behind the farmer portfolio (GET /api/portfolio/watchlist and /dashboard). Thin DB
// seam so the dashboard's price precedence, per-market anchoring and trend logic are unit-testable without
// a database; pure reads (AsNoTracking).
//
// OWNER SCOPING IS PART OF THE SIGNATURE. GetWatchlistAsync takes the caller's user id and there is no
// overload that returns rows for anyone else, so a handler cannot leak another farmer's crops even by
// mistake. The price and snapshot reads take an explicit crop-id set, which the handler only ever fills
// from the caller's own watchlist.
public interface IPortfolioReadStore
{
    // The caller's watchlist joined to crop display fields, with each crop's watched markets, ordered by
    // crop name. An empty list is a valid answer (the farmer has not added anything yet), never an error.
    Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(Guid userId, CancellationToken ct = default);

    // Newest usable ObservedDate at one market, PER CROP. Crops with no usable observation at that market
    // are simply absent from the result (an empty list means the market carries nothing for any of them).
    //
    // PER CROP IS THE WHOLE POINT. A single MAX across the crop set would make one crop's freshness depend
    // on its siblings: a crop last quoted in June would be measured against a crop quoted today and drop
    // out of its own window, so adding an unrelated crop to the watchlist would silently blank (or worse,
    // "fall back" for) a price that exists. Each crop is anchored on its own freshest data instead, and no
    // wall-clock staleness cutoff is applied anywhere.
    Task<IReadOnlyList<CropLatestObservation>> GetLatestObservedDatesAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default);

    // Usable observations at that market for each (crop, fromInclusive) window — one window per crop, cut
    // from that crop's OWN latest observation. "Usable" is the same fail-closed filter the price-history
    // and market-overview stores apply (IsUnitConfirmed = 1); the nullable price columns are coalesced to
    // 0, where 0 means ABSENT to the handler's precedence rules.
    Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
        IReadOnlyCollection<CropObservationWindow> windows, Guid marketId,
        CancellationToken ct = default);

    // The newest ForecastSnapshots row per crop — the FROZEN prediction columns only. The error/actual
    // columns are deliberately not projected: the farmer dashboard shows what was forecast, never how a
    // past forecast scored (that is the admin accuracy surface). Crops with no snapshot are simply absent.
    Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
        IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default);

    // One market's display fields, or null when the id resolves to nothing.
    Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default);

    // The Dedicated Economic Centre — the national price anchor (Dambulla, the only Markets row with
    // IsEconomicCenter = 1). The dashboard uses it for ONE thing: the single default block a crop with no
    // chosen markets gets. It is never a fallback for a market the farmer DID choose. Null only if no
    // market carries the flag, which the seed and the AddIsEconomicCenterToMarket backfill guarantee it
    // does.
    Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default);

    // Existence check for the validators, so a bad crop id is a structured 400 rather than a raw FK error.
    Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default);

    // THE SALES LOG (PRD 5.3, quarantined by PRD 3.1).
    //
    // Owner scoping is in the signature here too, and it matters more than anywhere else on this interface:
    // a farmer's sale prices are the most private data the product holds. There is no overload that reads
    // sales without a user id, and no aggregate across users — nothing here can be reached by anything but
    // the farmer who typed it.
    //
    // These rows go to the farmer's own screens ONLY. Nothing in this file is ever consumed by the feature
    // or training path; the Python loader is statically forbidden from naming the table at all.

    // One page of the caller's sales, newest first (SaleDate DESC, CreatedAtUtc DESC, then Id DESC purely
    // so the order is TOTAL and paging cannot repeat or skip a row). cropId narrows the page to one crop —
    // that is the More-details popup's per-crop list; null is the all-sales page. Total is the count BEFORE
    // paging and AFTER the crop filter, so the UI's page count matches what it filtered for.
    Task<UserSalesPage> GetSalesPageAsync(
        Guid userId, Guid? cropId, int page, int pageSize, CancellationToken ct = default);

    // One sale belonging to THIS user, or null when the id is unknown or somebody else's — the caller
    // cannot tell those apart, and both answer 404. Used for the POST/PUT read-back so a write response
    // carries exactly the shape the list endpoint returns.
    Task<UserSaleRow?> GetSaleAsync(Guid userId, Guid saleId, CancellationToken ct = default);
}

// One sale flattened for the DTO, with the crop and (optional) market display fields joined in.
//
// MarketId/MarketName/MarketShortCode are all null together when the farmer did not say where they sold —
// a normal state, not missing data. Note is the farmer's own free text and travels ONLY to its owner: it is
// never rendered into an audit line, never aggregated, never read by the ML layer.
public sealed record UserSaleRow(
    Guid Id,
    Guid CropId,
    string CropName,
    string? CropCode,
    Guid? MarketId,
    string? MarketName,
    string? MarketShortCode,
    DateOnly SaleDate,
    decimal PricePerKg,
    decimal? QuantityKg,
    string? Note,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

// One page of sales plus the total matching row count. Same shape as the admin logs pages
// (UserActivityPage), so the two paging surfaces cannot drift apart.
public sealed record UserSalesPage(IReadOnlyList<UserSaleRow> Items, int Total);

// One watchlist row flattened for the list DTO, with the crop's watched markets attached.
//
// Markets is ordered oldest-chosen first and may be EMPTY: a farmer who has not picked a market for a crop
// is a normal state, read as the national / economic-centre default, not as missing data. PlantedDate is
// likewise null when the farmer has not told us.
public sealed record WatchlistRow(
    Guid CropId,
    string CropName,
    string? CropCode,
    DateOnly? PlantedDate,
    IReadOnlyList<WatchlistMarketRow> Markets,
    DateTime CreatedAtUtc);

// One market a crop is watched at, with the display fields the farmer UI shows. ShortCode is the
// display-only chip label from Markets.ShortCode — never a key, and empty when unassigned.
public sealed record WatchlistMarketRow(Guid MarketId, string Name, string ShortCode);

// One crop's freshest usable observation date at one market — the anchor its own trend window is cut from.
public sealed record CropLatestObservation(Guid CropId, DateOnly LatestDate);

// One crop's observation window: everything at/after FromInclusive for that crop. Windows are per crop
// precisely so a stale crop's window is not widened (or narrowed) by a fresher sibling's.
public sealed record CropObservationWindow(Guid CropId, DateOnly FromInclusive);

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
// already the wire spelling on GET /api/markets/get/all. ShortCode is the display-only chip label, empty
// when unassigned — the dashboard's default-market block renders it, so it is projected here rather than
// fetched separately.
public sealed record PortfolioMarketRow(Guid Id, string Name, string ShortCode, bool IsEconomicCenter);
