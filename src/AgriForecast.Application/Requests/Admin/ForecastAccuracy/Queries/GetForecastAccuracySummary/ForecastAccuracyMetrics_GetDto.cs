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

    // Of the scored rows, those also carrying the referencePrice (and actualPrice) the baseline formula
    // needs — the ANCHORED rows: the denominator of baselineMape, baselineMedianApe, skillVsBaseline
    // and predictionEqualsReferenceShare. Rows without a plant-day anchor are excluded, not scored as
    // zero-error, and this count makes the exclusion visible.
    public int BaselineScoredCount { get; set; }

    // MAPE of DOING NOTHING: score the carry-forward referencePrice as if it were the prediction,
    // computed to the same convention as the stored percentageError (percent units,
    // |ref − actual| / max(|actual|, 1e-6) — the same denominator clip the Python maturing pass uses,
    // so an actual of zero yields a huge-but-finite figure, never an error). This is the number the
    // model has to beat — a model error near baselineMape is a model adding nothing over the price
    // already known on plant day.
    public decimal? BaselineMape { get; set; }

    // Median absolute percentage error of the same do-nothing baseline, the robust companion to
    // baselineMape exactly as medianApe is to mape.
    public decimal? BaselineMedianApe { get; set; }

    // The model's MAPE over the anchored rows, divided by baselineMape (ratio of the two 2-dp-rounded
    // figures). Skill is measured over the N = baselineScoredCount rows where both the prediction and
    // the do-nothing anchor are measurable — never a mix of populations, so when some scored rows are
    // anchorless the numerator is NOT the headline mape above. Read it as: below 1.0 the model beats
    // carrying the plant-day price forward; above 1.0 doing nothing would have been MORE accurate;
    // exactly 1.0 the served predictions are indistinguishable from the carry-forward. Null when
    // baselineScoredCount is 0 or baselineMape is 0 — the latter meaning the baseline mean rounds to
    // zero at 2 dp, so the ratio is unpublishable at page precision, not that the baseline was perfect.
    public decimal? SkillVsBaseline { get; set; }

    // Of the ANCHORED rows (baselineScoredCount), those where the served prediction made no claim
    // independent of the carry-forward anchor: predictedPrice is value-equal to referencePrice
    // (decimal equality, scale-insensitive; both stored decimal(10,2), so equality is meaningful).
    // Anchorless rows are unmeasured, not non-copies — there was no anchor to compare against. A high
    // count means the "accuracy" above is largely the baseline wearing a model's name.
    public int PredictionEqualsReferenceCount { get; set; }

    // The same as a share of the anchored rows (baselineScoredCount), 0..1. Null when no scored row
    // carries an anchor. A share of 1.0 means no anchored prediction in this group ever departed from
    // the carry-forward price.
    public decimal? PredictionEqualsReferenceShare { get; set; }

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
    // plant-day reference price, 0..1. Computed over directionalScored rows ONLY — degenerate and
    // excluded rows contribute neither hits nor misses. Null when nothing is scorable: an empty
    // population reports null, never a fabricated 0 or 1 (measured values of 0 and 1 are real).
    public decimal? DirectionalAccuracy { get; set; }

    // Rows the directional figure was computed over. Every matured row lands in exactly one of
    // scored / degenerate / excluded, so the three counts must be shown wherever directionalAccuracy is.
    public int DirectionalScored { get; set; }

    // Assessable rows whose predictedPrice is value-equal to referencePrice: the predicted move is
    // exactly zero, so there is no direction to score. Counted here, never scored. Equality cannot
    // distinguish a fallback COPY of the carry-forward anchor from a model genuinely forecasting
    // "no change" — for fallback-served groups these are copies; for model groups read it only as
    // "no directional opinion". NOT the same population as predictionEqualsReferenceCount: that
    // count runs over anchored rows (both prices AND stored error columns), this one over all
    // price-complete rows, so the two can legitimately differ. A directional accuracy shown
    // alongside a high degenerate count was previously inflated by these rows: each one scored a
    // "hit" whenever the actual price sat exactly flat, so the figure measured price stasis, not
    // model skill.
    public int DirectionalDegenerate { get; set; }

    // Rows excluded because no reference or actual price was available. NOT counted as misses — there
    // was no direction to get right or wrong about.
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
