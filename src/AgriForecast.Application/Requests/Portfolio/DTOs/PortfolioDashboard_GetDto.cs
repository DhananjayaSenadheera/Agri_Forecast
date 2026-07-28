namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for GET /api/portfolio/dashboard — one item per crop the caller watches, and inside each crop
// one block per market that crop is watched at.
//
// Every leg is FAIL-SOFT decoration over the watchlist (PRD §3.7): a crop always appears, every one of its
// markets always appears, and a missing price or prediction is null with a reason code rather than a
// fabricated number. An empty watchlist is a 200 with an empty Items list, never a 404.
//
// THERE IS NO TOP-LEVEL HOME MARKET. Markets are per crop, so "the market this dashboard is for" is not a
// question with one answer any more; each crop names its own markets and each market block carries its own
// price. The retired field is not kept as a null placeholder — a field that is always null is a field the
// UI will eventually read as meaningful.
public class PortfolioDashboard_GetDto
{
    public List<PortfolioDashboardItem_GetDto> Items { get; set; } = new();
}

// One watched crop, with a block per watched market.
public class PortfolioDashboardItem_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string? CropCode { get; set; }

    // The farmer's own planting day, yyyy-MM-dd, or null when they have not recorded one. A date STRING,
    // not an instant: a planting day has no time and no timezone.
    public string? PlantedDate { get; set; }

    // One block per market this crop is watched at, in the farmer's own first-added-first order —
    // Markets[0] is what the UI shows first. NEVER EMPTY: a crop with no chosen market gets exactly one
    // block for the economic centre, flagged IsDefaultMarket, so the card is never price-empty while data
    // exists.
    public List<PortfolioDashboardMarket_GetDto> Markets { get; set; } = new();

    // Newest frozen forecast snapshot, or null with PredictionUnavailableReason set. ONE PER CROP, not per
    // market: the model serves a single national, Dambulla-anchored price and never a per-market one.
    public PortfolioPrediction_GetDto? Prediction { get; set; }
    public string? PredictionUnavailableReason { get; set; }
}

// One market a crop is watched at, and that market's own price.
public class PortfolioDashboardMarket_GetDto
{
    public Guid MarketId { get; set; }
    public string Name { get; set; } = string.Empty;

    // The short chip label (e.g. "DEC", "KEP"); display-only and possibly empty. The UI keeps addressing
    // markets by MarketId.
    public string ShortCode { get; set; } = string.Empty;

    // True ONLY for the economic-centre block a crop with no chosen markets gets. It is a DEFAULT, not a
    // failure and not a substitution: the farmer never picked a market, so this is the national anchor
    // standing in, and the UI should say so rather than imply their own market let them down.
    public bool IsDefaultMarket { get; set; }

    // Latest observed price + trend AT THIS MARKET, or null with PriceUnavailableReason set.
    //
    // NEVER SUBSTITUTED. If the farmer chose this market and it has published nothing usable for this crop,
    // the answer is null + no_recent_price — not another market's number. Showing Dambulla's price under a
    // Keppetipola tab would be a lie about where the farmer can sell.
    public PortfolioPrice_GetDto? Price { get; set; }
    public string? PriceUnavailableReason { get; set; }
}

// The observed-price leg: a real published price at ONE market, never a forecast.
public class PortfolioPrice_GetDto
{
    public decimal Price { get; set; }

    // yyyy-MM-dd. Always rendered next to the price: the block shows the freshest price that EXISTS for
    // THIS crop AT THIS MARKET — no staleness cutoff, no dependence on how fresh the farmer's other crops
    // are, and no dependence on this crop's OTHER markets — so the date is how the farmer judges how
    // current it is. It can be months old; that is a fact about the market's publishing, and the UI must
    // show it as such rather than hide the price.
    public string ObservedDate { get; set; } = string.Empty;

    // "up" / "down" / "steady" versus the immediately previous observation AT THE SAME MARKET, or null when
    // there is no earlier observation within 30 days of THIS (crop, market)'s own latest one to compare
    // against. Null is not "steady", and a null direction never means the price beside it is missing or
    // unreliable.
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
