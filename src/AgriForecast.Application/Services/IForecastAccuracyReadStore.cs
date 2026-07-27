namespace AgriForecast.Application.Services;

// Read-only projection over ForecastSnapshots for the admin "Forecast accuracy" surface. Thin DB seam so
// the aggregation maths is unit-testable without a database; pure reads (AsNoTracking), no writes — the
// nightly Python job owns every write to this table.
public interface IForecastAccuracyReadStore
{
    // Row counts per maturity state plus the newest SnapshotDate on file. One cheap grouped count; an
    // empty table yields all-zero counts and a null date, never an error.
    Task<ForecastSnapshotCensus> GetCensusAsync(CancellationToken ct = default);

    // The scoring columns of MATURED rows only — the only rows accuracy may be computed from. pending
    // rows have no actual yet, actual_unavailable rows were never scored, and not_maturable rows can
    // never be scored; including any of them would silently dilute the aggregates.
    //
    // Deliberately a lean row projection rather than a SQL GROUP BY: the headline metric is the MEDIAN
    // absolute percentage error, which EF cannot translate, and computing half the metrics in SQL and
    // half in memory is how the two drift apart. At ~96 crops/day (~35k rows a year, of which only the
    // matured subset lands here) eight scalar columns per row is a small read for an admin-only page.
    Task<IReadOnlyList<ForecastSnapshotScoringRow>> GetMaturedScoringRowsAsync(CancellationToken ct = default);

    // Page of snapshot rows, newest SnapshotDate first (Id DESC tiebreak), plus the total matching count.
    // 1-based paging. Filters are AND-combined; a null filter is NO filter. maturedOnly narrows to the
    // matured state alone (not "everything that reached a terminal state").
    Task<ForecastSnapshotsPage> GetSnapshotsPageAsync(
        int page,
        int pageSize,
        Guid? cropId,
        string? modelVersion,
        bool maturedOnly,
        CancellationToken ct = default);
}

// Row counts per maturity state. Total is counted independently of the four buckets so that a state the
// DB check constraint somehow let through still shows up as an arithmetic gap rather than vanishing.
public sealed record ForecastSnapshotCensus(
    int Total,
    int Pending,
    int Matured,
    int ActualUnavailable,
    int NotMaturable,
    DateOnly? LatestSnapshotDate);

// The scoring columns of one matured snapshot.
//
// The error columns are read AS STORED. They are the frozen record of how that prediction actually
// scored, written once by the maturing pass; recomputing them here from prices would let a .NET rounding
// rule quietly disagree with the ledger. The three PRICE columns are carried only for directional
// accuracy, which is a comparison against ReferencePrice and has no stored column of its own.
public sealed record ForecastSnapshotScoringRow(
    string ActivePredictor,
    string? ModelVersion,
    decimal PredictedPrice,
    decimal? ActualPrice,
    decimal? ReferencePrice,
    decimal? SignedError,
    decimal? PercentageError,
    bool? WithinInterval);

// One ForecastSnapshots row projected for the admin list, with the crop's display fields joined on.
public sealed record ForecastSnapshotListRow(
    Guid Id,
    Guid CropId,
    string CropName,
    string? CropCode,
    DateOnly SnapshotDate,
    DateOnly? HarvestDate,
    int? GrowthPeriodDays,
    decimal PredictedPrice,
    decimal LowerBound,
    decimal UpperBound,
    decimal? ReferencePrice,
    string Confidence,
    string ActivePredictor,
    string? FallbackTier,
    string? ModelVersion,
    string? ReasonCode,
    string MaturityState,
    decimal? ActualPrice,
    DateOnly? ActualObservedDate,
    decimal? SignedError,
    decimal? AbsoluteError,
    decimal? PercentageError,
    bool? WithinInterval,
    DateTime CreatedAtUtc,
    DateTime? MaturedAtUtc);

public sealed record ForecastSnapshotsPage(IReadOnlyList<ForecastSnapshotListRow> Items, int Total);
