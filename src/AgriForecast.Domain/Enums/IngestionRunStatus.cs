namespace AgriForecast.Domain.Enums;

// Terminal (and in-flight) status of a single ingestion RUN row — one per source per pass.
// Stored as int (HasConversion<int>); the numeric values are pinned by test and a reorder would
// silently corrupt persisted rows (the fix for a reorder belongs in a migration, never here).
//
// Running   = the Running row was inserted and the source is executing (or the process crashed
//             before it could be finalized — a null FinishedUtc on an old Running row is the
//             "crashed mid-pass" breadcrumb).
// Succeeded = the source ran to completion without throwing. Counts (if the source reports them)
//             are attached; a status-only source (weather/economic/news) leaves them null.
// Failed    = the source threw. ErrorSummary carries a SANITIZED note (type + truncated message),
//             never a stack trace or path.
// Skipped   = the source was a deliberate no-op this pass (e.g. a disabled source). Defined for
//             the schema + Python/verification side; the Worker itself currently only emits
//             Running -> Succeeded / Failed.
public enum IngestionRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Skipped = 3
}
