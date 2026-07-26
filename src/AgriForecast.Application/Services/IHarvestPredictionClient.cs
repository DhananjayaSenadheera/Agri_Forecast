namespace AgriForecast.Application.Services;

// Typed client over the Python ML service. Implemented in Infrastructure; returns null on any failure so
// the caller can fail safe with a structured error.
public interface IHarvestPredictionClient
{
    Task<HarvestPredictionDto?> PredictAsync(Guid cropId, DateOnly plantDate, CancellationToken ct = default);

    Task<CropTimelineDto?> GetTimelineAsync(Guid cropId, DateOnly? asOf, int months, CancellationToken ct = default);

    Task<CropReadinessDto?> GetCropReadinessAsync(CancellationToken ct = default);

    // A not-rankable response is a valid result (Rankable=false plus a reason), not a failure; only null
    // means the ML service could not be reached.
    Task<HarvestWindowDto?> GetHarvestWindowAsync(Guid cropId, DateOnly? asOf, int horizonDays, CancellationToken ct = default);
}
