namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// The accuracy metrics of ONE group of matured snapshots. Never returned on its own — it always hangs
// off a group whose key names the active predictor it describes.
//
// Every metric is nullable and every metric ships its own denominator. A null means "not measurable
// from these rows", which is a different statement from 0 and must render differently. Since the
// minimum-sample gate, a null can ALSO mean "measurable but not publishable — too few rows": EACH
// RATE IS GATED ON THE DENOMINATOR PRINTED BESIDE IT (mape/medianApe/signedBias on scoredCount; the
// baseline pair and skillVsBaseline on baselineScoredCount; directionalAccuracy on
// directionalScored; intervalCoverage(+gap) on intervalScoredCount — one threshold,
// minScoredCountForMetrics, against each; the macro pair alone has TWO bars, rows AND crops — see
// macroMape below). Every count stays populated either way, so the page can print "n=16, below the
// 30-row minimum" instead of a metric computed over 16 rows dressed up as a verdict.
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

    // Distinct crops among the scored rows — the macro-average denominator, and a disclosure in its
    // own right: a group whose 40 rows are 1 crop is a different fact from 40 rows across 20 crops.
    public int DistinctCropCount { get; set; }

    // The MACRO companions to mape/medianApe, over the same scored rows: each crop's own figure first,
    // then the mean of those per-crop figures with equal CROP weight (distinctCropCount of them).
    // mape/medianApe above are MICRO — every row equal, so the heaviest crop dominates. Micro answers
    // "how wrong is a typical prediction"; macro answers "how wrong is a typical crop". The two
    // diverging means a few heavy crops dominate the pooled figure — read the macro before believing
    // the micro speaks for the crop list.
    //
    // TWO publication bars, ROWS and CROPS, both stated: scoredCount >= minScoredCountForMetrics
    // (the rows feeding the per-crop figures must be adequate) AND distinctCropCount >= 3
    // (MinDistinctCropsForMacro). A mean over fewer than 3 crops is not a "typical crop" — at 1 crop
    // it merely duplicates micro, and at 2 a single crop (even a single ROW) carries half the
    // crop-weight — so this pair can be null while mape/medianApe publish; distinctCropCount beside
    // it says why.
    public decimal? MacroMape { get; set; }
    public decimal? MacroMedianApe { get; set; }

    // Growth-period span of the SCORED rows, in days; null when nothing is scored (or the writer
    // stored no growth period). The survivorship disclosure: /summary windows on snapshotDate but a
    // row matures only at snapshotDate + growthPeriodDays, so a 30-day window whose group shows max 45
    // is saying long-horizon crops CANNOT be in these numbers yet — the metrics describe fast-growing
    // crops only.
    public int? MinGrowthPeriodDays { get; set; }
    public int? MaxGrowthPeriodDays { get; set; }

    // The minimum-sample gate's HEADLINE decision: scoredCount >= minScoredCountForMetrics. Each rate
    // is gated on ITS OWN denominator (see the header), so this flag and an individual rate can
    // legitimately disagree: true with directionalAccuracy null (30 scored rows, 2 of them
    // directionally scorable), or false with intervalCoverage published (10 scored rows, 210 interval
    // verdicts). Read it as "the headline error metrics are publishable", nothing more. All counts,
    // the growth-period span and predictionEqualsReferenceShare (a fact about what the predictions
    // ARE, not a skill estimate) stay populated, so the page renders the population and the reason
    // instead of a number. NOTE: this flag does NOT distinguish "not measurable" from "not
    // publishable" — an empty group (maturedCount 0, scoredCount 0) reads false exactly like a gated
    // one; a page that wants to say "no data yet" rather than "below the minimum" must read
    // maturedCount too.
    public bool MeetsMinimumSample { get; set; }

    // The threshold in effect (the production constant; echoed so the page can explain the nulls
    // without hard-coding 30).
    public int MinScoredCountForMetrics { get; set; }
}

// The lifecycle census of ONE group, over the same windowDays as the group's metrics. This is what
// makes a group with nothing matured yet EXIST on the page: 525 pending rows and maturedCount 0 means
// "serving, not yet scored" — without these counts that group would be absent and unreadable from
// "this predictor doesn't exist". Expect groups whose census has rows but whose metrics are all null;
// until a group's first rows mature, that is the correct shape, not an inconsistency.
public class ForecastSnapshotGroupCensus_GetDto
{
    // All rows of this group in the window, summed independently of the four buckets below —
    // mirroring the top-level counts, a state the DB somehow let through shows as an arithmetic gap.
    public int Total { get; set; }

    // OPEN: taken, not yet due. The rows the group's future scores will come from.
    public int Pending { get; set; }

    // Scored. Matches the group's metrics.maturedCount (both reads cover the same window). A row
    // maturing between the two queries can only put this ONE ABOVE metrics.maturedCount — the matured
    // rows are read first and maturing never removes or re-keys a row — and metrics.maturedCount
    // remains the denominator the metrics were actually computed over.
    public int Matured { get; set; }

    // Due but never matched to an actual price. Rows this group will now never be scored on.
    public int ActualUnavailable { get; set; }

    // No resolvable growth period, so nothing to score against. Terminal from creation.
    public int NotMaturable { get; set; }

    // yyyy-MM-dd of the earliest harvest date among this group's still-PENDING rows; null when the
    // group has no pending rows (nothing in flight). NOT a forward-looking promise, and never clamped
    // to today. A FUTURE date means the first real score becomes possible then — "first ML scores
    // possible from 2026-09-15" instead of an unexplained empty group. A PAST date means pending rows
    // are already OVERDUE: a row is scored on harvest day only if a price published that exact day;
    // otherwise it waits out the maturity grace window, and if the nightly sweep stops running the
    // date freezes in the past. Read a past date as "overdue rows / check the sweep", never "score
    // coming".
    public string? EarliestScoreableHarvestDate { get; set; }
}

// Aggregates for one active predictor, across every model version.
public class PredictorAccuracy_GetDto
{
    // As served and as stored, e.g. "residual" / "crop_mean_fallback". Never normalised into a
    // friendlier label here — the raw value is what the ML side reports and what a comparison against
    // its logs has to match.
    public string ActivePredictor { get; set; } = string.Empty;

    // Every state, so the group exists as soon as it has ANY rows; the metrics below stay
    // matured-rows-only.
    public ForecastSnapshotGroupCensus_GetDto Census { get; set; } = new();

    public ForecastAccuracyMetrics_GetDto Metrics { get; set; } = new();
}

// Aggregates for one (active predictor, horizon bucket) pair — the matured rows bucketed by the
// growth period they were served with: "short" (< 60 days), "medium" (60–120 inclusive), "long"
// (> 120), plus "unknown" only when a matured row somehow carries no growth period (a writer defect,
// surfaced rather than dropped). Every predictor with matured rows carries all three named buckets,
// empty ones included: an empty long bucket in a short window IS the survivorship message — no
// long-horizon crop can have matured into this window yet.
public class HorizonBucketAccuracy_GetDto
{
    // Part of the key, not a label: a bucket pooled across predictors would be the blended number the
    // split law forbids (see ForecastAccuracyMath).
    public string ActivePredictor { get; set; } = string.Empty;

    // One of "short" / "medium" / "long" / "unknown" — lowercase wire keys (the ForecastAccuracyMath
    // HorizonBucket* constants); display text belongs to the FE.
    public string HorizonBucket { get; set; } = string.Empty;

    // Matured rows of this predictor whose growth period falls in the bucket. No census here: the
    // horizon breakdown is a survey of the matured survivors by design (the survivorship it exposes is
    // about what HAS matured), and the predictor's full census already lives on byActivePredictor.
    public ForecastAccuracyMetrics_GetDto Metrics { get; set; } = new();
}

// One (activePredictor, crop) entry in the worst-crops ranking: that predictor's OWN figures for the
// crop over its NON-COPY scored rows in the window. The predictor is part of the key, not a label —
// an entry pooling a crop's predictors would be the blended number the split law (PRD §3.4) forbids.
// Ranking within the one list is fine because every entry names its predictor; a reader can filter.
public class WorstCropAccuracy_GetDto
{
    public string ActivePredictor { get; set; } = string.Empty;

    public Guid CropId { get; set; }

    // The crop's CURRENT name at read time (the Crops join), NOT the name at snapshot time — a
    // renamed crop shows its new name against old rows (same convention as the read store's
    // ForecastSnapshotScoringRow note).
    public string CropName { get; set; } = string.Empty;

    // All scored rows for this (predictor, crop) in the window — the population disclosure. The
    // figures below are computed over the NON-COPY subset, scoredCount − copyCount rows: only pairs
    // with at least worstCropMinScoredCount of THOSE are ranked.
    public int ScoredCount { get; set; }

    // Of the scored rows, the copies (prediction value-equal to the carry-forward anchor — the same
    // convention as predictionEqualsReferenceCount). Excluded from the figures below: a copy has
    // APE ≈ 0 by construction, so counting copies drags a crop's median toward zero and demotes
    // exactly the crops farmers are most misled about. A crop whose rows are ALL copies has no
    // measured forecasts and cannot appear here at all.
    public int CopyCount { get; set; }

    // The ranking key (descending): the crop's typical measured miss, robust to one spiked day.
    //
    // NB: medianApe/mape here are the ONLY magnitude figures on the wire that publish BELOW the
    // 30-row bar — an entry publishes from worstCropMinScoredCount (5) non-copy rows, badged (never
    // masked) by meetsMinimumSample below. The entry does not carry the 30 itself; the page reads it
    // off minScoredCountForMetrics on any metrics object in the same response.
    public decimal MedianApe { get; set; }

    // Shown beside it; a medianApe ≪ mape means a few extreme days, not chronic misses.
    public decimal Mape { get; set; }

    // Whether the non-copy population (scoredCount − copyCount) reaches minScoredCountForMetrics —
    // the same 30-row bar the group metrics publish under. Entries below it are RANKED but should be
    // badged as small-sample: the list is ordinal triage with a disclosed n, and hiding a small-n
    // entry would un-rank the very crops the list exists to surface.
    public bool MeetsMinimumSample { get; set; }
}

// Aggregates for one (model version, active predictor) pair.
public class ModelVersionAccuracy_GetDto
{
    // e.g. "v17". Null for rows served without a recorded version; those rows are kept as their own
    // group rather than folded into another version's numbers.
    public string? ModelVersion { get; set; }

    // Part of the key, not a label: see ForecastAccuracyMath's split law.
    public string ActivePredictor { get; set; } = string.Empty;

    // Every state, so the group exists as soon as it has ANY rows; the metrics below stay
    // matured-rows-only.
    public ForecastSnapshotGroupCensus_GetDto Census { get; set; } = new();

    public ForecastAccuracyMetrics_GetDto Metrics { get; set; } = new();
}
