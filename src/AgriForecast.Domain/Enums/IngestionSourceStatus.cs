namespace AgriForecast.Domain.Enums;

// Operational status of a per-source ingestion watermark. Stored as int.
//
// Ok        = the source ran to a successful completion at least once; LastSuccessUtc is
//             the resume point and any failures since have NOT overwritten it (a failed pass
//             leaves the last-good watermark intact so the next pass resumes, not restarts).
// Disabled  = the source is deliberately switched OFF (e.g. CBSL, whose Python parser is not
//             implemented yet). A Disabled source is a NO-OP in the pass and, critically, is
//             NEVER counted as a source *failure* — it must not pollute the fail-isolation
//             signal or trip alerting for a source we chose not to run.
// Failed    = the last attempt threw. The watermark is NOT advanced (LastSuccessUtc stays at
//             the previous good value); this status is a breadcrumb for diagnostics only and
//             the pass still resumes from LastSuccessUtc on the next run.
public enum IngestionSourceStatus
{
    Ok = 0,
    Disabled = 1,
    Failed = 2
}
