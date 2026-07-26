namespace AgriForecast.Application.common;

// Magic-string values from the Python /predict contract, centralized so the honesty cap fails closed if
// the ML wording or casing ever drifts. Compare with OrdinalIgnoreCase.
public static class MlContract
{
    // ActivePredictor value emitted when the model falls back to the per-crop mean.
    public const string FallbackPredictor = "crop_mean_fallback";

    // Confidence value emitted for a low-trust prediction.
    public const string LowConfidence = "Low";
}
