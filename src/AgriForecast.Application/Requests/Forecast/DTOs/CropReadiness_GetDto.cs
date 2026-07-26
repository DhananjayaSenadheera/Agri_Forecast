namespace AgriForecast.Application.Requests.Forecast.DTOs;

// FE-facing shape of GET /api/forecast/crop-readiness. The ML service's GUID-keyed map is flattened to a
// typed list. ready=true means the model serves this crop (history gate passed); false means it is
// fallback-served while price history is still collecting. A crop absent from the list has no payload
// entry at all, and the FE treats absence exactly like ready=false. When ModelActive is false every
// entry is ready=false, so the UI shows its honest "collecting" state rather than a fabricated green.
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
