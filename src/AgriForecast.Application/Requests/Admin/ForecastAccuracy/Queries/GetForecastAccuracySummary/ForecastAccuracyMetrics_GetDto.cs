namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// The accuracy metrics of ONE group of matured snapshots. Never returned on its own — it always hangs
// off a group whose key names the active predictor it describes.
//
// Every metric is nullable and every metric ships its own denominator. A null means "not measurable
// from these rows", which is a different statement from 0 and must render differently.
public class ForecastAccuracyMetrics_GetDto
{
    // Matured rows in this group — the population.
    public int MaturedCount { get; set; }

    // Of those, the rows carrying BOTH a percentage error and a signed error. It is the denominator of
    // mape, medianApe AND signedBias: the three are computed over that one shared filter, so the
    // denominators coincide by construction rather than by luck. Below maturedCount only if the maturing
    // pass left an error column null, which is worth seeing rather than smoothing over.
    public int ScoredCount { get; set; }

    // Mean absolute percentage error, in PERCENT units (12.5 = 12.5%). Sensitive to outliers, which on
    // low-priced crops a single spike can dominate — read medianApe first.
    public decimal? Mape { get; set; }

    // THE HEADLINE. Median absolute percentage error, in PERCENT units: the typical miss, unmoved by
    // the handful of extreme days that make MAPE look worse than the farmer's experience of it.
    public decimal? MedianApe { get; set; }

    // Mean SIGNED error in Rs/kg (predicted − actual), so the sign is meaningful: positive = the
    // forecasts run high (over-promising the farmer), negative = they run low. Never take its absolute
    // value; equal and opposite misses cancelling to ~0 is exactly the fact this metric reports.
    public decimal? SignedBias { get; set; }

    // Rows carrying a withinInterval verdict — the coverage denominator.
    public int IntervalScoredCount { get; set; }
    public int WithinIntervalCount { get; set; }

    // Share of actuals that landed inside the p10–p90 band, 0..1.
    public decimal? IntervalCoverage { get; set; }

    // What the band CLAIMS to be (0.80). Sent with every group so coverage is never read without its
    // yardstick.
    public decimal NominalIntervalCoverage { get; set; }

    // intervalCoverage − nominalIntervalCoverage. Negative = the band is too narrow (overconfident:
    // more actuals fall outside than promised). Positive = too wide (honest but uninformative).
    public decimal? IntervalCoverageGap { get; set; }

    // Share of rows where the forecast called the direction of the move correctly against the
    // plant-day reference price, 0..1.
    public decimal? DirectionalAccuracy { get; set; }

    // Rows the directional figure was computed over, and rows excluded from it because no reference or
    // actual price was available. Excluded rows are NOT counted as misses — there was no direction to
    // get right — so this pair must be shown wherever directionalAccuracy is.
    public int DirectionalScored { get; set; }
    public int DirectionalExcluded { get; set; }
}

// Aggregates for one active predictor, across every model version.
public class PredictorAccuracy_GetDto
{
    // As served and as stored, e.g. "residual" / "crop_mean_fallback". Never normalised into a
    // friendlier label here — the raw value is what the ML side reports and what a comparison against
    // its logs has to match.
    public string ActivePredictor { get; set; } = string.Empty;

    public ForecastAccuracyMetrics_GetDto Metrics { get; set; } = new();
}

// Aggregates for one (model version, active predictor) pair.
public class ModelVersionAccuracy_GetDto
{
    // e.g. "v17". Null for rows served without a recorded version; those rows are kept as their own
    // group rather than folded into another version's numbers.
    public string? ModelVersion { get; set; }

    // Part of the key, not a label: see ForecastAccuracyMath's split law.
    public string ActivePredictor { get; set; } = string.Empty;

    public ForecastAccuracyMetrics_GetDto Metrics { get; set; } = new();
}
