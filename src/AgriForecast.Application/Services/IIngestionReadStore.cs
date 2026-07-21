using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over the ingestion audit tables (IngestionRuns, IngestionVerifications,
// IngestionWatermarks) for the admin ingestion page (AdminIngestionController). Thin DB seam so
// the GetIngestionStatus / GetIngestionRuns handlers are unit-testable with canned rows
// (mirrors IIndicatorReadStore / IMarketReadStore).
//
// ISOLATION NOTE: unlike IIngestionRunRepository (which deliberately commits every WRITE in its
// own short-lived scope so an audit save can never flush a pass's half-done entities), these are
// pure READS on the normal request-scoped, AsNoTracking DbContext — reads need no isolation.
public interface IIngestionReadStore
{
    // ── STATUS ──────────────────────────────────────────────────────────────────────
    // Total number of IngestionRun rows. 0 => the "unknown" state (nothing has ever run).
    Task<int> GetRunCountAsync(CancellationToken ct = default);

    // Max StartedUtc among runs with FinishedUtc == null (an unfinished / in-flight or crashed
    // row), or null if none is unfinished. The handler compares this to the staleness window: a
    // FRESH unfinished row => "running"; a STALE one (older than the window) => crashed => "stopped".
    Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(CancellationToken ct = default);

    // The most recent run by StartedUtc (its BatchId + StartedUtc), or null if the table is empty.
    // BatchId is used to aggregate the latest pass's outcome.
    Task<IngestionRunHeadRow?> GetLatestRunAsync(CancellationToken ct = default);

    // The IngestionRunStatus of every source row in a batch (for the latest-batch outcome roll-up).
    Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
        Guid batchId, CancellationToken ct = default);

    // The latest IngestionVerification row by RunUtc, or null (the table can be empty until the
    // Python writer — PR 2 — lands).
    Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default);

    // Every per-source watermark row (ordered by Source) for the status "sources" list.
    Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default);

    // ── RUNS (server-paged) ───────────────────────────────────────────────────────────
    // Page of runs newest StartedUtc first, optionally filtered to one Source, plus the total
    // count of matching rows (for the pager). page is 1-based; the store applies Skip/Take.
    Task<IngestionRunsPage> GetRunsPageAsync(
        int page, int pageSize, string? source, CancellationToken ct = default);

    // The LATEST IngestionVerification (by RunUtc) for each of the given BatchIds, keyed by BatchId.
    // BatchIds with no verification are simply absent from the dictionary. Empty input => empty map.
    Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
        IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default);
}

// The head of the most recent run: just the fields needed to anchor the status roll-up.
public sealed record IngestionRunHeadRow(Guid BatchId, DateTime StartedUtc);

// One IngestionRun row projected for the admin runs list. Status stays the domain enum; the
// handler maps it to the lowercase wire string.
public sealed record IngestionRunRow(
    Guid Id,
    Guid BatchId,
    string Source,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    IngestionRunStatus Status,
    DateOnly? CoveredFromDate,
    DateOnly? CoveredToDate,
    int? RowsFetched,
    int? RowsInserted,
    int? RowsSkipped,
    int? DistinctCrops,
    string? ErrorSummary);

// One IngestionVerification row projected for the status summary + runs join. ChecksJson is passed
// through verbatim (already sanitized at write time on the Python side).
public sealed record IngestionVerificationRow(
    Guid? BatchId,
    IngestionVerificationStatus OverallStatus,
    DateTime RunUtc,
    DateOnly PipelineDate,
    int NChecksPass,
    int NChecksWarn,
    int NChecksFail,
    string ChecksJson);

// One IngestionWatermark row projected for the status "sources" list.
public sealed record IngestionWatermarkRow(
    string Source,
    IngestionSourceStatus Status,
    DateTime? LastSuccessUtc,
    DateOnly? LastObservedDate,
    string? LastMessage);

// A page of runs + the total matching count.
public sealed record IngestionRunsPage(IReadOnlyList<IngestionRunRow> Items, int Total);
