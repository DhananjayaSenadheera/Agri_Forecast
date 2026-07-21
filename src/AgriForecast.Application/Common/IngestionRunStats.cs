namespace AgriForecast.Application.common;

// The terminal OUTCOME a source reports back to the Worker's run-tracking audit. Distinct from the
// persisted Domain enum IngestionRunStatus: this is the in-process signal a fail-safe source (one
// that never throws to the Worker, e.g. HARTI) uses to say "I actually failed / skipped" so the run
// row does not render a real failure as a green Succeeded row.
public enum IngestionRunOutcome
{
    Succeeded = 0,
    Failed = 1,
    Skipped = 2
}

// Per-source ingestion result a source returns from IngestAsync so the Worker can attach it to the
// source's IngestionRun row. A source that does not report counts either keeps the void-shaped
// signature (status-only sources) or returns an empty instance (all nulls) — nulls are the honest
// "not tracked" value, never a fabricated zero.
//
// Outcome makes a fail-safe source EXPRESSIVE: a source that swallows its own transport/parse errors
// (never throwing to the Worker) still reports Outcome=Failed + a FailureReason so the run row is
// marked Failed, not Succeeded. Outcome defaults to Succeeded so ordinary success sites need not set
// it. Count fields mirror IngestionRun's nullable count columns 1:1; all optional.
public record IngestionRunStats(
    DateOnly? CoveredFromDate = null,
    DateOnly? CoveredToDate = null,
    int? RowsFetched = null,
    int? RowsInserted = null,
    int? RowsSkipped = null,
    int? DistinctCrops = null,
    IngestionRunOutcome Outcome = IngestionRunOutcome.Succeeded,
    // Short reason recorded on the run row when Outcome=Failed (sanitized before storage). Ignored
    // for Succeeded / Skipped.
    string? FailureReason = null);
