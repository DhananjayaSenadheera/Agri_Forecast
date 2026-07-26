using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Common;

// Enum -> wire-string mapping for the admin ingestion DTOs, so the exact strings the FE consumes live in
// one place. The casing is part of the contract: run and source statuses are lowercase
// (running|succeeded|failed|skipped and ok|disabled|failed), verification status is title-case
// (Pass|Warn|Fail). The DTOs expose plain strings so JSON never emits an enum int.
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
