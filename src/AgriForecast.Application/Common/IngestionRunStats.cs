namespace AgriForecast.Application.common;

// The terminal outcome a source reports back to the Worker's run-tracking audit. Distinct from the
// persisted IngestionRunStatus: it lets a fail-safe source that never throws still say it failed, so
// the run row is not rendered as a green Succeeded.
public enum IngestionRunOutcome
{
    Succeeded = 0,
    Failed = 1,
    Skipped = 2
}

// Per-source ingestion result returned from IngestAsync so the Worker can attach it to the run row.
// Null counts are the honest "not tracked" value, never a fabricated zero. Outcome defaults to
// Succeeded, so a source that swallows its own errors must set Outcome=Failed plus a FailureReason.
public record IngestionRunStats(
    DateOnly? CoveredFromDate = null,
    DateOnly? CoveredToDate = null,
    int? RowsFetched = null,
    int? RowsInserted = null,
    int? RowsSkipped = null,
    int? DistinctCrops = null,
    IngestionRunOutcome Outcome = IngestionRunOutcome.Succeeded,
    // Short reason recorded on the run row when Outcome=Failed; ignored otherwise.
    string? FailureReason = null);
