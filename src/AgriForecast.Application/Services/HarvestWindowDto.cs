namespace AgriForecast.Application.Services;

// The Python ML POST /harvest-window response, consumed verbatim: candidate planting dates ranked by the
// price their harvest is forecast to fetch. CurrentPrice is the one .NET-side addition.
// Rankable is the field that matters. When it is false there is no window and no points — only a
// ReasonCode and a farmer-readable Explanation, which is the honest answer for a crop whose forecast
// cannot tell one date from another. Never synthesise a window on this side to fill the gap.
public sealed class HarvestWindowDto
{
    public Guid CropId { get; set; }
    public string? CropName { get; set; }
    public DateOnly AsOf { get; set; }
    public int? GrowthPeriodDays { get; set; }

    // Today's price, filled in by the .NET handler using the same CurrentPriceRule as the harvest forecast,
    // so the two screens can never quote different prices. 0 means unknown, and the UI hides the
    // comparison rather than pretending the window beats a price we do not have.
    public decimal CurrentPrice { get; set; }

    // false => Points is empty and Best is null; show ReasonCode/Explanation.
    public bool Rankable { get; set; }

    // "ml_served" when rankable; otherwise "no_growth_period", "no_feature_row", "model_inactive",
    // "crop_not_model_served", "scoring_failed", "flat_curve" or "unavailable".
    public string ReasonCode { get; set; } = string.Empty;

    public string ActivePredictor { get; set; } = string.Empty; // "model" | "residual" | "unavailable"
    public string Confidence { get; set; } = string.Empty;      // "Low" | "Medium" | "High"
    public string? ModelVersion { get; set; }
    public string Explanation { get; set; } = string.Empty;

    // Length of the recommended window in days; null when not rankable.
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

    // Gain over planting at an average date in the swept horizon — deliberately not against the worst date
    // (which would inflate it) nor against today (whose sign would flip as the sweep rolls forward).
    public decimal UpliftPct { get; set; }
}
