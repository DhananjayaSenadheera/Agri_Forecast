using System.Text.Json;

namespace AgriForecast.Application.Services;

// Mirrors the Python POST /timeline response contract verbatim. confidence, activePredictor and
// explanation pass straight through — never upgrade a Low or fallback result into a confident number.
// Crops with no history return an empty history list and Low confidence.
public sealed class CropTimelineDto
{
    public string? CropName { get; set; }
    public string ActivePredictor { get; set; } = string.Empty;  // "model" | "crop_mean_fallback" | "unavailable" (defensive no-history/error shape)
    public string Confidence { get; set; } = string.Empty;       // "Low" | "Medium" | "High"
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // Optional so an older ML service without these keys still deserializes. /timeline emits
    // reasonCode and reasonParams but never topFactors.
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
