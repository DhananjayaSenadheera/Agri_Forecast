namespace AgriForecast.Application.Services;

// Mirrors the Python FastAPI GET /crop-readiness response contract verbatim.
// `Crops` is keyed by LOWERCASE crop GUID string (trainer convention). `Ready`
// mirrors the real serving decision (active payload AND history-gate pass) —
// never re-derive it here. The ML service returns the honest empty shape
// (ModelActive=false, empty Crops) when no model is registered or on any
// internal failure; that is a valid response, not an error.
public sealed class CropReadinessDto
{
    public string? ModelVersion { get; set; }
    public int? MinHistoryObs { get; set; }
    public bool ModelActive { get; set; }
    public Dictionary<string, CropReadinessEntryDto> Crops { get; set; } = new();
}

public sealed class CropReadinessEntryDto
{
    public bool Ready { get; set; }

    // Labelled-row count from the model payload's per-crop fallback stats.
    // Null = unknown (old payloads) — never coalesce to 0, which would read
    // as "no data collected".
    public int? NObs { get; set; }
}
