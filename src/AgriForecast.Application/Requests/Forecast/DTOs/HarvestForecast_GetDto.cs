using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Forecast.DTOs;

// Farmer-facing harvest forecast + go/no-go recommendation.
// The model's prediction fields and confidence / activePredictor / explanation
// pass straight through untouched; recommendationLevel + reason are ours.
public class HarvestForecast_GetDto
{
    public Guid CropId { get; set; }
    public string? CropName { get; set; }
    public DateOnly PlantDate { get; set; }
    public string? HarvestDate { get; set; }
    public int? GrowthPeriodDays { get; set; }

    // Current price = average of the daily mid (Min+Max)/2 over the trailing 14
    // rows as of the plant date (fallback: latest day).
    public decimal CurrentPrice { get; set; }

    // Passed through from the model verbatim.
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string ActivePredictor { get; set; } = string.Empty;
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // Our recommendation matrix output.
    public RecommendationLevel RecommendationLevel { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal UpsidePct { get; set; }
    public decimal IntervalWidthPct { get; set; }
    public bool LowTrust { get; set; }
}
