namespace AgriForecast.Application.Services;

// Mirrors the Python GET /crop-readiness response contract verbatim. Crops is keyed by lowercase crop
// GUID string. Ready mirrors the real serving decision — never re-derive it here. The honest empty shape
// (ModelActive=false, empty Crops) is a valid response, not an error.
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

    // Labelled-row count from the model payload. Null means unknown; never coalesce to 0, which would read
    // as "no data collected".
    public int? NObs { get; set; }
}
