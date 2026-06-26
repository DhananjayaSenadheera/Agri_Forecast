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
}
