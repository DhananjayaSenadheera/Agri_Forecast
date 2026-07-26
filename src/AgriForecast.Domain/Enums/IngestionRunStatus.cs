namespace AgriForecast.Domain.Enums;

// Status of a single ingestion run row. Stored as int — the numeric values are persisted, so never
// renumber or reorder them (fix a mistake in a migration instead).
//
// Running   = the row was inserted and the source is executing; an old Running row with a null
//             FinishedUtc is the "crashed mid-pass" breadcrumb.
// Succeeded = the source completed without throwing.
// Failed    = the source threw; ErrorSummary carries a sanitized note.
// Skipped   = a deliberate no-op this pass (e.g. a disabled source), not a failure.
public enum IngestionRunStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Skipped = 3
}
