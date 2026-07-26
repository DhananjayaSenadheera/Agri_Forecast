namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// Response for GET /api/admin/ingestion/status. Status fields are pre-rendered strings (see
// IngestionStatusStrings) so the wire shape is frozen and never an enum int.
public class IngestionStatus_GetDto
{
    // "running" | "stopped" | "unknown". A stale unfinished run counts as crashed (stopped), never as a
    // fake "running"; "unknown" means the runs table is empty.
    public string State { get; set; } = string.Empty;

    // Echoed from the API's own config (Ingestion:ServiceAddress); "unconfigured" when unset.
    public string ServiceAddress { get; set; } = string.Empty;

    // Most recent IngestionRuns.StartedUtc, or null when no runs exist.
    public DateTime? LastRunAtUtc { get; set; }

    // Aggregated outcome of the most recent batch: "succeeded" | "partial" | "failed", or null when no
    // runs exist. An in-flight batch reports "partial" until it resolves.
    public string? LastRunStatus { get; set; }

    // True only when a pass started by THIS API process is in flight, i.e. when POST service/stop would be
    // accepted rather than answered 409 not_stoppable. Lets the admin UI disable Stop up front instead of
    // discovering after the click that the running pass belongs to the scheduled worker.
    //
    // ADVISORY, not a guarantee: the pass can finish between the poll and the click, so stop may still
    // answer not_running. And because the registry behind it is per-PROCESS, a multi-replica API would
    // report false on the replicas that are not hosting the pass (the deployment runs replicas: 1 today).
    // The endpoint remains the authority; this only shapes the button.
    public bool CanStop { get; set; }

    // Latest verification summary, or null while the verifications table is empty.
    public IngestionVerificationSummary_GetDto? LastVerification { get; set; }

    public List<IngestionSource_GetDto> Sources { get; set; } = new();
}

// Roll-up of the latest IngestionVerification row.
public class IngestionVerificationSummary_GetDto
{
    public string OverallStatus { get; set; } = string.Empty; // "Pass" | "Warn" | "Fail"
    public DateTime RanAtUtc { get; set; }
    public string PipelineDate { get; set; } = string.Empty;   // yyyy-MM-dd
    public int NChecksPass { get; set; }
    public int NChecksWarn { get; set; }
    public int NChecksFail { get; set; }
}

// One per-source watermark state.
public class IngestionSource_GetDto
{
    public string Source { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;        // "ok" | "disabled" | "failed"
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastObservedDate { get; set; }             // yyyy-MM-dd or null
    public string? LastMessage { get; set; }
}
