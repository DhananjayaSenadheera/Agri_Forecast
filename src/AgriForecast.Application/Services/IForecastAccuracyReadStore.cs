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
    // fromSnapshotDate BOUNDS the read (SnapshotDate >= it): the caller always passes a window, so this
    // never becomes a full-table scan as the ledger grows. It is a required parameter, not an optional
    // filter, precisely so that "no window" is not expressible here.
    //
    // Deliberately a lean row projection rather than a SQL GROUP BY: the headline metric is the MEDIAN
    // absolute percentage error, which EF cannot translate, and computing half the metrics in SQL and
    // half in memory is how the two drift apart. Eight scalar columns per row over a bounded window is
    // a small read for an admin-only page.
    Task<IReadOnlyList<ForecastSnapshotScoringRow>> GetMaturedScoringRowsAsync(
        DateOnly fromSnapshotDate, CancellationToken ct = default);

    // Census cells over ALL maturity states, one per (ActivePredictor, ModelVersion, MaturityState)
    // combination that has rows inside the window — this is what lets a predictor with 525 pending rows
    // and 0 matured ones exist on the summary at all, instead of being indistinguishable from a
    // predictor that never served. One grouped aggregate query; never a row-by-row read.
    //
    // fromSnapshotDate bounds the read exactly like GetMaturedScoringRowsAsync (SnapshotDate >= it), so
    // the census and the matured metrics always describe the SAME window. Within that window a cell can
    // exist with matured count 0 — a pending-only group is the expected shape until its first rows
    // mature, not an inconsistency.
    Task<IReadOnlyList<ForecastSnapshotGroupCensusRow>> GetGroupCensusAsync(
        DateOnly fromSnapshotDate, CancellationToken ct = default);

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

// One census cell: how many rows a (predictor, version) pair has in ONE maturity state, inside the
// caller's window. The states come back as stored; the aggregation layer buckets them ordinally, so a
// mis-cased state shows up as an arithmetic gap (in a group's Total but no bucket), never silently.
//
// EarliestHarvestDate is MIN(HarvestDate) over the cell's rows. It is only MEANINGFUL on the pending
// cell, where it is the earliest date a still-open row could first be scored (which may already be
// PAST — overdue rows keep their dates; see the aggregation layer's notes). A pending row carries a
// HarvestDate because the PYTHON WRITER enforces it: serving/snapshots.py assigns 'pending' only when
// a harvest date exists and mints 'not_maturable' otherwise. The entity factory's CreatePending guard
// is NOT the enforcer — it never runs on the production write path (the Python job writes raw SQL;
// see the DbContext's ForecastSnapshot remarks) — and no DB constraint ties state to HarvestDate,
// which is why the column stays nullable here and the aggregation still null-guards. HarvestDate is
// materialized on the row (SnapshotDate + GrowthPeriodDays as served), so nothing is re-derived here.
// On terminal cells it is just the min of already-resolved harvest dates and the aggregation layer
// ignores it.
public sealed record ForecastSnapshotGroupCensusRow(
    string ActivePredictor,
    string? ModelVersion,
    string MaturityState,
    int Count,
    DateOnly? EarliestHarvestDate);

// The scoring columns of one matured snapshot.
//
// The MODEL error columns are read AS STORED. They are the frozen record of how that prediction
// actually scored, written once by the maturing pass; recomputing them here from prices would let a
// .NET rounding rule quietly disagree with the ledger. The three PRICE columns feed the metrics that
// have no stored column of their own: directional accuracy (a comparison against ReferencePrice) and
// the do-nothing BASELINE, whose error is recomputed from ReferencePrice and ActualPrice by design —
// the ledger stores no baseline error, so there is nothing stored to disagree with (the formula
// mirrors the Python convention; see ForecastAccuracyMath).
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
