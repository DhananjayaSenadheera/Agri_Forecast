namespace AgriForecast.Application.Requests.Forecast.DTOs;

// FE-facing shape of GET /api/forecast/crop-readiness (crop-status colouring,
// UI feature 2026-07-22). The ML service's GUID-string-keyed map is flattened
// to a typed list so the FE consumes camelCase JSON with real Guids. `ready`
// mirrors the serving decision recorded in the promoted model payload:
//   true  -> the ML model serves this crop (history gate passed)
//   false -> fallback-served: still collecting price history
// A crop ABSENT from the list has no payload entry at all (brand new) — the FE
// treats absence exactly like ready=false. Recomputed by every train run, so
// a crop that crosses the data gate flips to ready at the next retrain
// automatically. ModelActive=false (payload not promoted / ML down) => every
// entry is ready=false; the FE shows its honest "collecting" state, never a
// fabricated green.
public sealed class CropReadiness_GetDto
{
    public string? ModelVersion { get; set; }
    public int? MinHistoryObs { get; set; }
    public bool ModelActive { get; set; }
    public List<CropReadinessItem_GetDto> Crops { get; set; } = new();
}

public sealed class CropReadinessItem_GetDto
{
    public Guid CropId { get; set; }
    public bool Ready { get; set; }
    public int? NObs { get; set; }
}
