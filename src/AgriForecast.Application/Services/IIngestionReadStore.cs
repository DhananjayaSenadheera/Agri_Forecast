using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over the ingestion audit tables (IngestionRuns, IngestionVerifications,
// IngestionWatermarks) for the admin ingestion page. Thin DB seam so the handlers are unit-testable.
// Unlike IIngestionRunRepository, which isolates every WRITE in its own scope, these are pure reads on the
// normal request-scoped AsNoTracking context and need no isolation.
public interface IIngestionReadStore
{
    // The status reads take an excludeSources set (null or empty excludes nothing). The policy of which
    // sources are not part of the ingestion service lives with the caller; the store only applies the set
    // it is handed. GetRunsPageAsync is deliberately NOT filtered — /runs lists every real run row.

    // Number of run rows outside excludeSources. 0 means the "unknown" state.
    Task<int> GetRunCountAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default);

    // Max StartedUtc among unfinished runs outside excludeSources, or null. The handler compares it to the
    // staleness window: a fresh row means running, a stale one means crashed.
    Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default);

    // The most recent run by StartedUtc outside excludeSources; its BatchId anchors the outcome roll-up.
    Task<IngestionRunHeadRow?> GetLatestRunAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default);

    // The status of every source row in a batch outside excludeSources. An all-excluded batch is empty.
    Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
        Guid batchId, IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default);

    // The latest verification row by RunUtc, or null while the table is empty.
    Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default);

    // Every per-source watermark row (ordered by Source) for the status "sources" list.
    Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default);

    // Page of runs newest StartedUtc first, optionally filtered to one Source, plus the total matching
    // count. page is 1-based; the store applies Skip/Take.
    Task<IngestionRunsPage> GetRunsPageAsync(
        int page, int pageSize, string? source, CancellationToken ct = default);

    // The latest verification for each given BatchId. BatchIds with no verification are simply absent.
    Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
        IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default);
}

// The head of the most recent run: just the fields needed to anchor the status roll-up.
public sealed record IngestionRunHeadRow(Guid BatchId, DateTime StartedUtc);

// One run row projected for the admin runs list. Status stays the domain enum; the handler maps it to the
// wire string.
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

// One verification row projected for the status summary and the runs join. ChecksJson is passed through
// verbatim.
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
