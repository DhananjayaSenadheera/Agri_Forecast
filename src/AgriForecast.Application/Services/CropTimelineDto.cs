using System.Text.Json;

namespace AgriForecast.Application.Services;

// Mirrors the Python FastAPI POST /timeline response contract verbatim.
// confidence / activePredictor / explanation pass straight through to the
// farmer - never upgrade a Low / fallback into a confident number.
// Crops with no history return an empty history list + Low confidence.
public sealed class CropTimelineDto
{
    public string? CropName { get; set; }
    public string ActivePredictor { get; set; } = string.Empty;  // "model" | "crop_mean_fallback" | "unavailable" (defensive no-history/error shape)
    public string Confidence { get; set; } = string.Empty;       // "Low" | "Medium" | "High"
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // --- Additive (API-5), OPTIONAL for old-ML-service compatibility. ---
    // /timeline emits reasonCode/reasonParams but NEVER topFactors (by design).
    public string? ReasonCode { get; set; }
    public Dictionary<string, JsonElement>? ReasonParams { get; set; }

    public List<TimelineHistoryPointDto> History { get; set; } = new();
    public List<TimelineForecastPointDto> Forecast { get; set; } = new();
}

public sealed class TimelineHistoryPointDto
{
    public string Month { get; set; } = string.Empty;  // "YYYY-MM"
    public decimal AvgPrice { get; set; }
}

public sealed class TimelineForecastPointDto
{
    public int HorizonMonths { get; set; }
    public string Date { get; set; } = string.Empty;   // "YYYY-MM-DD"
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
}
