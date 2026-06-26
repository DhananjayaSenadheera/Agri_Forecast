namespace AgriForecast.Application.Services;

// Typed client over the Python ML service POST /predict.
// Implemented in Infrastructure (ExternalSources/Clients). Returns null on any
// failure so the caller can fail safe with a structured error.
public interface IHarvestPredictionClient
{
    Task<HarvestPredictionDto?> PredictAsync(Guid cropId, DateOnly plantDate, CancellationToken ct = default);
}
