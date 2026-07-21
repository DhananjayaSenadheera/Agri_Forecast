namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// Response for GET /api/admin/ingestion/status. A single at-a-glance snapshot of ingestion health
// for the admin page. All status fields are pre-rendered strings (see IngestionStatusStrings) so the
// wire shape is frozen and never an enum int. PascalCase C# names serialize to camelCase JSON
// (the API's default System.Text.Json policy), matching the other admin endpoints.
public class IngestionStatus_GetDto
{
    // "running" | "stopped" | "unknown". running = a fresh unfinished run exists within the
    // staleness window; stopped = runs exist but none is unfinished-fresh (a stale unfinished row is
    // treated as crashed, NEVER a fake "running"); unknown = the runs table is empty.
    public string State { get; set; } = string.Empty;

    // Echoed from the API's own config (Ingestion:ServiceAddress); "unconfigured" when unset.
    public string ServiceAddress { get; set; } = string.Empty;

    // Most recent IngestionRuns.StartedUtc, or null when no runs exist.
    public DateTime? LastRunAtUtc { get; set; }

    // Aggregated outcome of the most recent BatchId: "succeeded" | "partial" | "failed", or null
    // when no runs exist. A still-in-flight batch (a Running row, no failures) reports "partial"
    // until it resolves — never a premature "succeeded".
    public string? LastRunStatus { get; set; }

    // Latest verification summary, or null (the verifications table can be empty until the Python
    // writer lands).
    public IngestionVerificationSummary_GetDto? LastVerification { get; set; }

    // Per-source watermark states.
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
