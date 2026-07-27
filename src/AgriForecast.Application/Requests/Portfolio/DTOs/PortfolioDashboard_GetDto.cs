namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for GET /api/portfolio/dashboard — one item per crop the caller watches, plus the one home
// market those prices are shown for.
//
// Both legs of every item are FAIL-SOFT decoration over the watchlist (PRD §3.7): a crop always appears,
// even when neither a price nor a prediction is available, and each missing leg is null with a reason code
// rather than a fabricated number. An empty watchlist is a 200 with an empty Items list, never a 404.
public class PortfolioDashboard_GetDto
{
    // The market the prices below are shown for. Null only if the database has no economic centre and the
    // farmer has chosen no market — in practice never, since Dambulla carries IsEconomicCenter = 1.
    public PortfolioHomeMarket_GetDto? HomeMarket { get; set; }

    public List<PortfolioDashboardItem_GetDto> Items { get; set; } = new();
}

// The farmer's home market.
public class PortfolioHomeMarket_GetDto
{
    public Guid MarketId { get; set; }
    public string Name { get; set; } = string.Empty;

    // True for a Dedicated Economic Centre (today only Dambulla, the model's national price anchor).
    // FALSE IS LOAD-BEARING FOR THE UI: a prediction shown under a non-economic-centre home market must
    // carry the "National forecast" label, because the model serves one Dambulla-anchored national price
    // and never a per-market one (PRD §3.6).
    public bool IsEconomicCenter { get; set; }

    // True when the farmer has not chosen a market and this is the economic-centre default standing in.
    public bool IsDefault { get; set; }
}

// One watched crop.
public class PortfolioDashboardItem_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string? CropCode { get; set; }

    // Latest observed price + trend, or null with PriceUnavailableReason set.
    public PortfolioPrice_GetDto? Price { get; set; }
    public string? PriceUnavailableReason { get; set; }

    // Newest frozen forecast snapshot, or null with PredictionUnavailableReason set.
    public PortfolioPrediction_GetDto? Prediction { get; set; }
    public string? PredictionUnavailableReason { get; set; }
}

// The observed-price leg: a real published price, never a forecast.
public class PortfolioPrice_GetDto
{
    public decimal Price { get; set; }

    // yyyy-MM-dd. Always rendered next to the price: the dashboard shows the freshest price that EXISTS for
    // THIS crop at that market — no staleness cutoff, and no dependence on how fresh the farmer's other
    // watched crops happen to be — so the date is how the farmer judges how current it is. It can be
    // months old; that is a fact about the market's publishing, and the UI must show it as such rather
    // than hide the price.
    public string ObservedDate { get; set; } = string.Empty;

    // Which market actually served this price — NOT necessarily the home market.
    public Guid MarketId { get; set; }
    public string MarketName { get; set; } = string.Empty;

    // True when the home market had no usable observation for this crop and the price came from the
    // economic centre instead. The UI must label it; silently showing another market's price as the
    // farmer's own would be dishonest.
    public bool IsFallbackMarket { get; set; }

    // "up" / "down" / "steady" versus the immediately previous observation at the SAME market, or null when
    // there is no earlier observation within 30 days of THIS crop's own latest one to compare against. Null
    // is not "steady", and a null direction never means the price beside it is missing or unreliable.
    public string? Direction { get; set; }

    // Signed percent change against PreviousPrice, 1 decimal place. Null whenever Direction is null.
    public decimal? ChangePct { get; set; }

    public decimal? PreviousPrice { get; set; }
    public string? PreviousObservedDate { get; set; }
}

// The prediction leg: the newest ForecastSnapshots row, read verbatim.
//
// FROZEN FIELDS ONLY. No actual price, no error columns: this is what the model said, not how it scored.
// Confidence and ActivePredictor pass through exactly as served — a Low-confidence fallback stays Low and
// is shown de-rated, never hidden and never upgraded into a confident-looking number.
public class PortfolioPrediction_GetDto
{
    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }

    // "Low" / "Medium" / "High", verbatim.
    public string Confidence { get; set; } = string.Empty;

    // e.g. "residual" / "crop_mean_fallback", verbatim.
    public string ActivePredictor { get; set; } = string.Empty;

    public string? ModelVersion { get; set; }

    // yyyy-MM-dd. SnapshotDate is the plant date the forecast assumed; HarvestDate is null when the
    // crop's growth period could not be resolved.
    public string SnapshotDate { get; set; } = string.Empty;
    public string? HarvestDate { get; set; }
}
