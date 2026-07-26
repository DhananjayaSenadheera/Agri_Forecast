namespace AgriForecast.Domain.Enums;

// Operational status of a per-source ingestion watermark. Stored as int.
//
// Ok       = the source has completed successfully at least once; LastSuccessUtc is the resume point.
// Disabled = deliberately switched off. A Disabled source is a no-op and is never counted as a failure.
// Failed   = the last attempt threw. LastSuccessUtc keeps its previous good value so the next pass
//            resumes rather than restarts.
public enum IngestionSourceStatus
{
    Ok = 0,
    Disabled = 1,
    Failed = 2
}
