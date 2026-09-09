namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// Response for GET /api/admin/forecast-accuracy/summary.
//
// There is NO top-level "accuracy" field and there never will be one: accuracy exists only inside a
// predictor group (PRD §3.4). Anything an admin can read as "the model is X% accurate" has to name
// which predictor produced it.
public class ForecastAccuracySummary_GetDto
{
    // When this summary was computed, so the admin page can show the staleness of its own poll.
    public DateTime GeneratedAtUtc { get; set; }

    // The window the AGGREGATES cover: matured rows whose snapshotDate falls in the last N days. Echoed
    // back so a number is never read without knowing what it spans — the same MAPE means something very
    // different over 30 days than over 10 years.
    public int WindowDays { get; set; }

    // yyyy-MM-dd of the newest snapshot on file, or null when the table is empty. This is the nightly
    // job's heartbeat: a date that stops advancing means the snapshot pass has stopped running, which
    // no accuracy metric would otherwise reveal.
    public string? LatestSnapshotDate { get; set; }

    // Row census across every maturity state. Counts, unlike accuracy, are not split by predictor —
    // they describe the ledger, not the model's skill.
    //
    // DELIBERATE ASYMMETRY: counts are ALL-TIME, the metrics below are WINDOWED. The census answers "is
    // the nightly job running and is the ledger healthy?", which a window would hide — a pile of
    // actual_unavailable rows from eighteen months ago is still a fact about the pipeline. The metrics
    // answer "how is the model doing lately?", which all-time history would blur. The group CENSUS
    // below is windowed too, so the asymmetry covers every state, not just matured: counts.matured vs
    // the groups' summed maturedCount, and likewise counts.pending / actualUnavailable / notMaturable
    // / total vs the summed group census buckets (counts.pending against the summed census.pending is
    // the pair an admin is most likely to eyeball). Each all-time count is expected to meet or exceed
    // its windowed counterpart, and none of those gaps is a bug.
    public ForecastSnapshotCounts_GetDto Counts { get; set; } = new();

    // One entry per active predictor (e.g. "residual" vs "crop_mean_fallback") with ANY snapshot rows
    // inside windowDays — a CENSUS, not a survey of the matured survivors. A predictor still waiting on
    // its first maturity appears here with a live census, maturedCount 0 and null metrics ("not yet
    // scored"); absence from this list means the predictor served nothing in the window at all. The
    // METRICS inside each entry still cover the group's MATURED rows only. Empty only when nothing was
    // served in the window.
    public List<PredictorAccuracy_GetDto> ByActivePredictor { get; set; } = new();

    // The same census-based groups keyed by (modelVersion, activePredictor). The predictor stays in the
    // key: a version's rows are still split model-vs-fallback, because a version that mostly fell back
    // is not a version that was mostly right.
    public List<ModelVersionAccuracy_GetDto> ByModelVersion { get; set; } = new();

    // Matured rows bucketed by growth period, keyed by (activePredictor, horizonBucket) — the
    // survivorship breakdown: /summary windows on snapshotDate but a row matures only at snapshotDate
    // + growthPeriodDays, so a short window structurally contains fast-growing crops only, and pooling
    // them was audit Critical-3's Simpson's-paradox trap. Every predictor with ANY matured rows in the
    // window carries all three named buckets (short/medium/long), empty ones with zero counts and null
    // metrics; "unknown" appears only if a matured row has no growth period. Empty when nothing has
    // matured in the window at all.
    public List<HorizonBucketAccuracy_GetDto> ByHorizonBucket { get; set; } = new();

    // Up to 10 (activePredictor, crop) entries PER PREDICTOR — each predictor contributes up to
    // worstCropsMaxEntries of its own worst crops and the concatenation is re-sorted worst-first for
    // display (a single global cap let one predictor's bad crops starve the other's off the list
    // entirely) — ranked by their own medianApe over the window's NON-COPY scored rows, among pairs
    // with at least worstCropMinScoredCount of them. The predictor stays in the key (split law — a
    // crop entry pooling its predictors would blend
    // model-served and fallback-served rows), and copies (prediction == carry-forward anchor) never
    // qualify a crop nor move its figures: a crop whose rows are all copies has no measured forecasts
    // to rank. Empty therefore means "no (predictor, crop) pair has enough MEASURED forecasts yet" —
    // today's live all-fallback-copies data yields exactly this, and the empty list plus the exposed
    // threshold IS the honest answer, NOT "no crop is bad".
    public List<WorstCropAccuracy_GetDto> WorstCrops { get; set; } = new();

    // The qualification threshold in effect for worstCrops — minimum NON-COPY scored rows per
    // (predictor, crop) pair (the production constant, echoed so an empty list is explainable without
    // hard-coding 5 in the FE).
    public int WorstCropMinScoredCount { get; set; }
}

// Row counts per maturity state. The four states are the values in ForecastSnapshotMaturityStates.
public class ForecastSnapshotCounts_GetDto
{
    public int Total { get; set; }

    // OPEN: taken, not yet due. Everything else on this DTO is terminal.
    public int Pending { get; set; }

    // Scored — the only rows any aggregate is computed from.
    public int Matured { get; set; }

    // Due but never matched to an actual price inside the carry-back window. Surfaced rather than
    // dropped: a growing count here means the accuracy figures rest on a shrinking share of forecasts.
    public int ActualUnavailable { get; set; }

    // No resolvable growth period, so no harvest date to score against. Terminal from creation.
    public int NotMaturable { get; set; }
}
