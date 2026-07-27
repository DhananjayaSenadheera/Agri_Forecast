using AgriForecast.Application.Services;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;

/// <summary>
/// The accuracy aggregation, kept as pure functions over already-fetched matured rows so it can be
/// tested without a database and cannot drift between the two callers.
/// </summary>
/// <remarks>
/// THE SPLIT LAW (PRD §3.4): a combined, model-and-fallback-blended accuracy number is prohibited. There
/// is no "overall" entry point here on purpose — every public method returns metrics keyed by
/// ActivePredictor, so the only way to produce a blended figure is to write new code to do it. A model
/// that serves 11 crops and a crop-mean fallback that serves 85 average out to a number that describes
/// neither, and an admin reading it would credit the model with the fallback's behaviour.
///
/// The error columns are read as stored (see ForecastSnapshotScoringRow). PercentageError is SIGNED and
/// in PERCENT units, so the absolute percentage error is simply its magnitude.
/// </remarks>
public static class ForecastAccuracyMath
{
    /// <summary>
    /// The interval the model claims to be: p10..p90, i.e. 80% of actuals should land inside the band.
    /// Coverage is reported against this so an admin can see the GAP: well below = overconfident band,
    /// well above = uselessly wide.
    /// </summary>
    public const decimal NominalIntervalCoverage = 0.80m;

    // Rates (shares of rows) keep 4 dp; percent- and rupee-scale metrics keep 2 dp, which is already
    // finer than the underlying prices are quoted.
    private const int RateDecimals = 4;
    private const int MagnitudeDecimals = 2;

    /// <summary>
    /// Metrics for one group of matured rows. Every count is reported next to the metric it is the
    /// denominator for, so a figure computed over a handful of rows can never look like a verdict.
    /// ScoredCount is the denominator of Mape, MedianApe AND SignedBias — the three are computed over
    /// one shared row filter, not three coincidentally-similar ones.
    /// </summary>
    public sealed record AccuracyMetrics(
        int MaturedCount,
        int ScoredCount,
        decimal? Mape,
        decimal? MedianApe,
        decimal? SignedBias,
        int IntervalScoredCount,
        int WithinIntervalCount,
        decimal? IntervalCoverage,
        decimal? IntervalCoverageGap,
        decimal? DirectionalAccuracy,
        int DirectionalScored,
        int DirectionalExcluded);

    /// <summary>Metrics for one ActivePredictor across every model version.</summary>
    public sealed record PredictorGroup(string ActivePredictor, AccuracyMetrics Metrics);

    /// <summary>
    /// Metrics for one (ModelVersion, ActivePredictor) pair. The predictor is part of the key, not a
    /// detail: grouping by version ALONE would blend model-served and fallback-served rows of that
    /// version back into exactly the number the split law forbids. ModelVersion is null for rows served
    /// before a version was recorded.
    /// </summary>
    public sealed record ModelVersionGroup(string? ModelVersion, string ActivePredictor, AccuracyMetrics Metrics);

    /// <summary>Matured rows grouped by ActivePredictor, ordered by predictor name.</summary>
    public static List<PredictorGroup> ByPredictor(IEnumerable<ForecastSnapshotScoringRow> maturedRows) =>
        maturedRows
            .GroupBy(r => r.ActivePredictor, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new PredictorGroup(g.Key, Compute(g)))
            .ToList();

    /// <summary>
    /// Matured rows grouped by (ModelVersion, ActivePredictor). Ordering is LEXICAL on the version
    /// string, not semantic ("v9" sorts after "v17"); the admin UI sorts for display.
    /// </summary>
    public static List<ModelVersionGroup> ByModelVersion(IEnumerable<ForecastSnapshotScoringRow> maturedRows) =>
        maturedRows
            .GroupBy(r => (r.ModelVersion, r.ActivePredictor))
            .OrderBy(g => g.Key.ModelVersion is null) // rows with no recorded version sort last
            .ThenBy(g => g.Key.ModelVersion, StringComparer.Ordinal)
            .ThenBy(g => g.Key.ActivePredictor, StringComparer.Ordinal)
            .Select(g => new ModelVersionGroup(g.Key.ModelVersion, g.Key.ActivePredictor, Compute(g)))
            .ToList();

    /// <summary>
    /// The metrics for one already-grouped set of matured rows.
    /// </summary>
    /// <remarks>
    /// Every metric has its OWN denominator and its own null case. A matured row written by the Python
    /// job with a missing error column is not silently treated as a zero — it is excluded from that
    /// metric and visible as the gap between MaturedCount and the metric's count. An empty group yields
    /// null metrics with zero counts, never 0.0 dressed up as a measurement.
    ///
    /// The three error metrics share ONE row filter: a row counts towards mape, medianApe and
    /// signedBias only if BOTH PercentageError and SignedError are present. The maturing pass writes the
    /// two columns together, so in practice that filter changes nothing — but making it explicit is what
    /// lets ScoredCount be published as their common denominator. Deriving each metric from its own
    /// null-check would leave signedBias silently averaged over a different population than the count
    /// printed beside it.
    /// </remarks>
    public static AccuracyMetrics Compute(IEnumerable<ForecastSnapshotScoringRow> rows)
    {
        var list = rows as IReadOnlyList<ForecastSnapshotScoringRow> ?? rows.ToList();

        var scored = list
            .Where(r => r.PercentageError.HasValue && r.SignedError.HasValue)
            .ToList();

        // APE = |PercentageError|, already in percent units and already frozen at maturity.
        var apes = scored.Select(r => Math.Abs(r.PercentageError!.Value)).ToList();
        var signedErrors = scored.Select(r => r.SignedError!.Value).ToList();

        var intervalScored = list.Count(r => r.WithinInterval.HasValue);
        var withinInterval = list.Count(r => r.WithinInterval == true);

        decimal? coverage = intervalScored == 0
            ? null
            : Round((decimal)withinInterval / intervalScored, RateDecimals);

        var direction = Directional(list);

        return new AccuracyMetrics(
            MaturedCount: list.Count,
            ScoredCount: scored.Count,
            Mape: apes.Count == 0 ? null : Round(apes.Average(), MagnitudeDecimals),
            MedianApe: apes.Count == 0 ? null : Round(Median(apes), MagnitudeDecimals),
            SignedBias: signedErrors.Count == 0 ? null : Round(signedErrors.Average(), MagnitudeDecimals),
            IntervalScoredCount: intervalScored,
            WithinIntervalCount: withinInterval,
            IntervalCoverage: coverage,
            IntervalCoverageGap: coverage is null ? null : Round(coverage.Value - NominalIntervalCoverage, RateDecimals),
            DirectionalAccuracy: direction.Accuracy,
            DirectionalScored: direction.Scored,
            DirectionalExcluded: direction.Excluded);
    }

    /// <summary>
    /// Share of rows where the forecast got the DIRECTION of the move right, measured from the price
    /// known on plant day: predicted = sign(PredictedPrice − ReferencePrice), actual =
    /// sign(ActualPrice − ReferencePrice), and a row counts as a hit when the two signs agree.
    /// </summary>
    /// <remarks>
    /// Rows with a NULL ReferencePrice (no carry-forward anchor existed at snapshot time) or a NULL
    /// ActualPrice are EXCLUDED, not scored as misses — there is no direction to be right or wrong
    /// about. The excluded count is returned so the figure is never read as covering more rows than it
    /// does. Mirrors the Python evaluate.directional_accuracy contract
    /// {directional_acc, n_scored, n_excluded} with its default deadband of 0, so an exactly flat
    /// prediction (sign 0) is a hit only against an exactly flat actual.
    /// </remarks>
    private static (decimal? Accuracy, int Scored, int Excluded) Directional(
        IReadOnlyList<ForecastSnapshotScoringRow> rows)
    {
        var scorable = rows
            .Where(r => r.ReferencePrice.HasValue && r.ActualPrice.HasValue)
            .ToList();

        var excluded = rows.Count - scorable.Count;
        if (scorable.Count == 0)
            return (null, 0, excluded);

        var hits = scorable.Count(r =>
            Math.Sign(r.PredictedPrice - r.ReferencePrice!.Value) ==
            Math.Sign(r.ActualPrice!.Value - r.ReferencePrice!.Value));

        return (Round((decimal)hits / scorable.Count, RateDecimals), scorable.Count, excluded);
    }

    // Standard median: the middle value, or the mean of the two middle values on an even count.
    private static decimal Median(List<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : (sorted[mid - 1] + sorted[mid]) / 2m;
    }

    private static decimal Round(decimal value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero);
}
