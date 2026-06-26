namespace AgriForecast.Application.common;

// Canonical magic-string values from the Python ML /predict contract.
// Centralized so the honesty cap fails CLOSED if the ML wording/casing ever
// drifts - a fallback / Low-confidence prediction must never silently become a
// confident-looking recommendation. Compare with OrdinalIgnoreCase.
public static class MlContract
{
    // ActivePredictor value emitted when the model falls back to the per-crop mean.
    public const string FallbackPredictor = "crop_mean_fallback";

    // Confidence value emitted for a low-trust prediction.
    public const string LowConfidence = "Low";
}
