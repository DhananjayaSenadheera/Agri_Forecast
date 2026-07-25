namespace AgriForecast.Application.Services;

// Typed client over the Python ML service POST /predict.
// Implemented in Infrastructure (ExternalSources/Clients). Returns null on any
// failure so the caller can fail safe with a structured error.
public interface IHarvestPredictionClient
{
    Task<HarvestPredictionDto?> PredictAsync(Guid cropId, DateOnly plantDate, CancellationToken ct = default);

    Task<CropTimelineDto?> GetTimelineAsync(Guid cropId, DateOnly? asOf, int months, CancellationToken ct = default);

    Task<CropReadinessDto?> GetCropReadinessAsync(CancellationToken ct = default);

    // Best planting/harvest window. A not-rankable response is a VALID result
    // (Rankable=false + a reason), not a failure — only null means the ML service
    // could not be reached.
    Task<HarvestWindowDto?> GetHarvestWindowAsync(Guid cropId, DateOnly? asOf, int horizonDays, CancellationToken ct = default);
}
