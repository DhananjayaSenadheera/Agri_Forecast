namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// One run row for GET /api/admin/ingestion/runs. Status is a pre-rendered lowercase string, dates are
// yyyy-MM-dd strings, and counts stay null for a status-only source rather than being faked as 0.
// ErrorSummary is passed through as stored (already sanitized at write time).
public class IngestionRun_GetDto
{
    public Guid Id { get; set; }
    public Guid BatchId { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string Status { get; set; } = string.Empty; // "running" | "succeeded" | "failed" | "skipped"
    public string? CoveredFromDate { get; set; }        // yyyy-MM-dd or null
    public string? CoveredToDate { get; set; }          // yyyy-MM-dd or null
    public int? RowsFetched { get; set; }
    public int? RowsInserted { get; set; }
    public int? RowsSkipped { get; set; }
    public int? DistinctCrops { get; set; }
    public string? ErrorSummary { get; set; }
    public IngestionRunVerification_GetDto? Verification { get; set; }
}

// The verification joined onto a run by BatchId (latest per BatchId). checksJson is passed through
// verbatim for the FE to parse.
public class IngestionRunVerification_GetDto
{
    public string OverallStatus { get; set; } = string.Empty; // "Pass" | "Warn" | "Fail"
    public DateTime RanAtUtc { get; set; }
    public int NChecksPass { get; set; }
    public int NChecksWarn { get; set; }
    public int NChecksFail { get; set; }
    public string ChecksJson { get; set; } = string.Empty;
}
