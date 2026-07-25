namespace AgriForecast.Application.Services;

// Best harvest window — the Python ML service POST /harvest-window response,
// consumed VERBATIM (no reshaping, no derived fields). Ranks candidate planting
// dates by the price their harvest is forecast to fetch.
//
// THE CONTRACT THAT MATTERS: `Rankable`. When it is false there is NO window and
// NO points — only a `ReasonCode` and a farmer-readable `Explanation`. That is the
// honest answer for a crop whose forecast cannot distinguish one date from
// another (no verified growth period, model not serving the crop, or a curve too
// flat to carry a timing signal). Never synthesise a window on this side to fill
// the gap: a farmer can un-see a "no advice yet" message, but cannot un-plant.
public sealed class HarvestWindowDto
{
    public Guid CropId { get; set; }
    public string? CropName { get; set; }
    public DateOnly AsOf { get; set; }
    public int? GrowthPeriodDays { get; set; }

    // false => Points is empty and Best is null; show ReasonCode/Explanation.
    public bool Rankable { get; set; }

    // "ml_served" when rankable. Otherwise one of: "no_growth_period",
    // "no_feature_row", "model_inactive", "crop_not_model_served",
    // "scoring_failed", "flat_curve", "unavailable".
    public string ReasonCode { get; set; } = string.Empty;

    public string ActivePredictor { get; set; } = string.Empty; // "model" | "residual" | "unavailable"
    public string Confidence { get; set; } = string.Empty;      // "Low" | "Medium" | "High"
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // Length of the recommended window in days (the crop's HarvestWindowDays,
    // clamped). Null when not rankable.
    public int? WindowDays { get; set; }

    public List<HarvestWindowPointDto> Points { get; set; } = new();
    public HarvestWindowBestDto? Best { get; set; }
}

// One candidate planting date and the harvest it leads to.
public sealed class HarvestWindowPointDto
{
    public DateOnly PlantDate { get; set; }
    public DateOnly HarvestDate { get; set; }
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }
    public bool InBestWindow { get; set; }
}

// The recommended window itself.
public sealed class HarvestWindowBestDto
{
    public DateOnly PlantStart { get; set; }
    public DateOnly PlantEnd { get; set; }
    public DateOnly HarvestStart { get; set; }
    public DateOnly HarvestEnd { get; set; }
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }

    // Gain over planting at an AVERAGE date in the swept horizon — deliberately
    // not "vs the worst date" (which would inflate it) nor "vs today" (whose sign
    // would flip arbitrarily as the sweep window rolls forward).
    public decimal UpliftPct { get; set; }
}
