using System.Text.Json;

namespace AgriForecast.Application.Services;

// Mirrors the Python FastAPI POST /predict response contract verbatim.
// confidence / activePredictor / explanation are passed straight through to the
// farmer - never upgrade a Low / crop_mean_fallback into a confident number.
public sealed class HarvestPredictionDto
{
    public string CropId { get; set; } = string.Empty;
    public string? CropName { get; set; }
    public string PlantDate { get; set; } = string.Empty;
    public string? HarvestDate { get; set; }
    public int? GrowthPeriodDays { get; set; }
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
    public string Confidence { get; set; } = string.Empty;       // "Low" | "Medium" | "High"
    public string ActivePredictor { get; set; } = string.Empty;  // "model" | "crop_mean_fallback"
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // --- Additive (API-5), all OPTIONAL so an older ML service without these keys
    // still deserializes cleanly (nulls flow, nothing throws). ---

    // Machine-readable reason for the served path. ALWAYS present on a current ML
    // service; snake_case (e.g. "model_served", "not_model_served"). Pass through.
    public string? ReasonCode { get; set; }

    // Loose bag of camelCase params keyed by reasonCode (usually {}). Kept as a raw
    // JsonElement dictionary so new codes can add params without a .NET change and
    // values (which may be ints) survive round-trip byte-for-byte. Do NOT model rigidly.
    public Dictionary<string, JsonElement>? ReasonParams { get; set; }

    // SHAP-derived drivers. Present ONLY on the model-served path; the ML OMITS the
    // key entirely on any fallback path (and never emits it on /timeline). Stays
    // null when omitted so the FE can tell "no breakdown" (null) from "empty list".
    public List<TopFactorDto>? TopFactors { get; set; }
}

// One driver of the forecast. `direction` stays a plain string (no enum/converter) -
// values are "up" | "down" | "neutral". `weight` is a 0..1 share.
public sealed class TopFactorDto
{
    public string Code { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Weight { get; set; }
}
