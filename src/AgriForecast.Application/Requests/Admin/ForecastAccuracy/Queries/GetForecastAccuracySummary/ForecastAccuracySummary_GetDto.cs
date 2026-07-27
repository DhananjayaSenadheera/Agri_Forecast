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

    // yyyy-MM-dd of the newest snapshot on file, or null when the table is empty. This is the nightly
    // job's heartbeat: a date that stops advancing means the snapshot pass has stopped running, which
    // no accuracy metric would otherwise reveal.
    public string? LatestSnapshotDate { get; set; }

    // Row census across every maturity state. Counts, unlike accuracy, are not split by predictor —
    // they describe the ledger, not the model's skill.
    public ForecastSnapshotCounts_GetDto Counts { get; set; } = new();

    // Aggregates over MATURED rows only, one entry per active predictor (e.g. "residual" vs
    // "crop_mean_fallback"). Empty when nothing has matured yet.
    public List<PredictorAccuracy_GetDto> ByActivePredictor { get; set; } = new();

    // The same aggregates keyed by (modelVersion, activePredictor). The predictor stays in the key: a
    // version's rows are still split model-vs-fallback, because a version that mostly fell back is not
    // a version that was mostly right.
    public List<ModelVersionAccuracy_GetDto> ByModelVersion { get; set; } = new();
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
