using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Common;

// Central enum -> wire-string mapping for the admin ingestion DTOs, so the exact strings the FE
// consumes live in one place. Deliberate casing per the PR-3 contract:
//   * IngestionRunStatus        -> LOWERCASE (running|succeeded|failed|skipped)
//   * IngestionSourceStatus     -> LOWERCASE (ok|disabled|failed) — matches the run-status style
//   * IngestionVerificationStatus -> TITLE-CASE (Pass|Warn|Fail), exactly as the contract spells it
// The DTOs expose plain strings (never the enums) so System.Text.Json never emits an int and the
// wire values are frozen here, not at the serializer's mercy.
public static class IngestionStatusStrings
{
    public static string ToWire(IngestionRunStatus s) => s switch
    {
        IngestionRunStatus.Running => "running",
        IngestionRunStatus.Succeeded => "succeeded",
        IngestionRunStatus.Failed => "failed",
        IngestionRunStatus.Skipped => "skipped",
        _ => s.ToString().ToLowerInvariant()
    };

    public static string ToWire(IngestionSourceStatus s) => s switch
    {
        IngestionSourceStatus.Ok => "ok",
        IngestionSourceStatus.Disabled => "disabled",
        IngestionSourceStatus.Failed => "failed",
        _ => s.ToString().ToLowerInvariant()
    };

    // Pass|Warn|Fail — the enum names ARE the contract spelling; no re-casing.
    public static string ToWire(IngestionVerificationStatus s) => s.ToString();
}
