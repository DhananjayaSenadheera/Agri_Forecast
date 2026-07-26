using System.Text.Json;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Forecast.DTOs;

// Farmer-facing harvest forecast plus go/no-go recommendation. The model's prediction, confidence,
// activePredictor and explanation pass straight through; recommendationLevel and reason are ours.
public class HarvestForecast_GetDto
{
    public Guid CropId { get; set; }
    public string? CropName { get; set; }
    public DateOnly PlantDate { get; set; }
    public string? HarvestDate { get; set; }
    public int? GrowthPeriodDays { get; set; }

    // Average daily mid over the trailing 14 rows as of the plant date.
    public decimal CurrentPrice { get; set; }

    // Passed through from the model verbatim.
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string ActivePredictor { get; set; } = string.Empty;
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // Passed straight through from the ML service. reasonCode/reasonParams are always present from a
    // current service; topFactors is null when the ML omits it — the FE reads null as "no breakdown", so
    // it must never be replaced with an empty list.
    public string? ReasonCode { get; set; }
    public Dictionary<string, JsonElement>? ReasonParams { get; set; }
    public List<TopFactorDto>? TopFactors { get; set; }

    // Our recommendation matrix output.
    public RecommendationLevel RecommendationLevel { get; set; }
    public string Reason { get; set; } = string.Empty;
    public decimal UpsidePct { get; set; }
    public decimal IntervalWidthPct { get; set; }
    public bool LowTrust { get; set; }
}
