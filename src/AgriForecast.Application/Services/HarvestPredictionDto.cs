using System.Text.Json;

namespace AgriForecast.Application.Services;

// Mirrors the Python POST /predict response contract verbatim. confidence, activePredictor and
// explanation pass straight through — never upgrade a Low or crop_mean_fallback into a confident number.
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

    // Additive fields, all optional so an older ML service without these keys still deserializes.

    // Machine-readable reason for the served path, snake_case (e.g. "model_served"). Passed through.
    public string? ReasonCode { get; set; }

    // Loose bag of params keyed by reasonCode. Kept as raw JsonElement so new codes can add params without
    // a .NET change and the values survive round-trip unchanged. Do not model it rigidly.
    public Dictionary<string, JsonElement>? ReasonParams { get; set; }

    // SHAP-derived drivers, present only on the model-served path. Stays null when the ML omits the key,
    // so the FE can tell "no breakdown" from an empty list.
    public List<TopFactorDto>? TopFactors { get; set; }
}

// One driver of the forecast. direction is a plain string ("up" | "down" | "neutral") and weight is a
// 0..1 share.
public sealed class TopFactorDto
{
    public string Code { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public decimal Weight { get; set; }
}
