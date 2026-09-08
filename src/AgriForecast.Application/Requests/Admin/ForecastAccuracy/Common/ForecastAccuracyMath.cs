using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;

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
    ///
    /// BaselineScoredCount is the denominator of BaselineMape, BaselineMedianApe, SkillVsBaseline AND
    /// PredictionEqualsReferenceShare: the scored rows that also carry the ReferencePrice (and
    /// ActualPrice) the baseline formula needs. SkillVsBaseline compares like with like — skill is
    /// measured over the N = baselineScoredCount rows where both the prediction and the do-nothing
    /// anchor are measurable: the model MAPE restricted to those rows, divided by BaselineMape. Below
    /// 1.0 the model beats carrying the plant-day price forward, above 1.0 doing nothing would have
    /// been more accurate. Null when baselineScoredCount is 0 or BaselineMape is 0.
    /// PredictionEqualsReferenceCount/-Share (over BaselineScoredCount) are the anchored rows where
    /// the served prediction made no claim independent of the carry-forward anchor; anchorless rows
    /// are unmeasured, not non-copies.
    /// </summary>
    public sealed record AccuracyMetrics(
        int MaturedCount,
        int ScoredCount,
        decimal? Mape,
        decimal? MedianApe,
        decimal? SignedBias,
        int BaselineScoredCount,
        decimal? BaselineMape,
        decimal? BaselineMedianApe,
        decimal? SkillVsBaseline,
        int PredictionEqualsReferenceCount,
        decimal? PredictionEqualsReferenceShare,
        int IntervalScoredCount,
        int WithinIntervalCount,
        decimal? IntervalCoverage,
        decimal? IntervalCoverageGap,
        decimal? DirectionalAccuracy,
        int DirectionalScored,
        int DirectionalDegenerate,
        int DirectionalExcluded);

    /// <summary>
    /// The lifecycle census of one group, over the SAME window as its metrics. Total is summed
    /// independently of the four buckets, mirroring ForecastSnapshotCensus: a state the DB check
    /// constraint somehow let through shows up as an arithmetic gap, never a silent disappearance.
    /// EarliestScoreableHarvestDate is the earliest harvest date among the group's still-PENDING rows,
    /// null when nothing is pending. NOT a forward-looking promise: a FUTURE date means the first score
    /// becomes possible then; a PAST date means pending rows are already OVERDUE — still waiting on a
    /// published price inside the maturity grace window, or the nightly sweep has not run. It is
    /// deliberately never clamped to today; the past date IS the operational signal.
    /// </summary>
    public sealed record GroupCensus(
        int Total,
        int Pending,
        int Matured,
        int ActualUnavailable,
        int NotMaturable,
        DateOnly? EarliestScoreableHarvestDate);

    /// <summary>Census and metrics for one ActivePredictor across every model version.</summary>
    public sealed record PredictorGroup(string ActivePredictor, GroupCensus Census, AccuracyMetrics Metrics);

    /// <summary>
    /// Census and metrics for one (ModelVersion, ActivePredictor) pair. The predictor is part of the
    /// key, not a detail: grouping by version ALONE would blend model-served and fallback-served rows of
    /// that version back into exactly the number the split law forbids. ModelVersion is null for rows
    /// served before a version was recorded.
    /// </summary>
    public sealed record ModelVersionGroup(
        string? ModelVersion, string ActivePredictor, GroupCensus Census, AccuracyMetrics Metrics);

    /// <summary>
    /// Groups by ActivePredictor, ordered by predictor name. The group SET is a CENSUS over every
    /// snapshot state, not a survey of the matured survivors: a predictor whose rows are all still
    /// pending gets a group with a live census, MaturedCount 0 and null metrics — "not yet scored" —
    /// where grouping the matured rows alone would erase it and an admin could not tell "no data yet"
    /// from "no such predictor". Metrics still come from the matured rows ONLY, via the same Compute
    /// an all-matured group uses; an empty matured list yields nulls and zero counts by construction,
    /// never fabricated zeros.
    ///
    /// The keys are the UNION of the two reads' keys — a defence against key-set divergence between
    /// two independent reads generally: a matured key the census has no cells for still gets its
    /// group, with an all-zero census, rather than vanishing. A RACE cannot produce that direction —
    /// the handler reads matured FIRST and the census second, nothing deletes snapshot rows, and
    /// maturing never rewrites a row's ActivePredictor or ModelVersion, so a row maturing between the
    /// reads only puts census.Matured one ABOVE MaturedCount (documented on the census DTO). The
    /// realistic cause would be casing/whitespace divergence between this ordinal in-memory grouping
    /// and the SQL GROUP BY under the database's case-insensitive collation — the known, accepted gap
    /// noted in ForecastAccuracyReadStore.
    /// </summary>
    public static List<PredictorGroup> ByPredictor(
        IEnumerable<ForecastSnapshotScoringRow> maturedRows,
        IEnumerable<ForecastSnapshotGroupCensusRow> censusRows)
    {
        var matured = maturedRows.ToLookup(r => r.ActivePredictor, StringComparer.Ordinal);
        var census = censusRows.ToLookup(r => r.ActivePredictor, StringComparer.Ordinal);

        return census.Select(g => g.Key)
            .Union(matured.Select(g => g.Key), StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new PredictorGroup(k, CensusOf(census[k]), Compute(matured[k])))
            .ToList();
    }

    /// <summary>
    /// Groups by (ModelVersion, ActivePredictor), census-based exactly like ByPredictor. Ordering is
    /// LEXICAL on the version string, not semantic ("v9" sorts after "v17"); the admin UI sorts for
    /// display.
    /// </summary>
    public static List<ModelVersionGroup> ByModelVersion(
        IEnumerable<ForecastSnapshotScoringRow> maturedRows,
        IEnumerable<ForecastSnapshotGroupCensusRow> censusRows)
    {
        var matured = maturedRows.ToLookup(r => (r.ModelVersion, r.ActivePredictor));
        var census = censusRows.ToLookup(r => (r.ModelVersion, r.ActivePredictor));

        return census.Select(g => g.Key)
            .Union(matured.Select(g => g.Key))
            .OrderBy(k => k.ModelVersion is null) // rows with no recorded version sort last
            .ThenBy(k => k.ModelVersion, StringComparer.Ordinal)
            .ThenBy(k => k.ActivePredictor, StringComparer.Ordinal)
            .Select(k => new ModelVersionGroup(
                k.ModelVersion, k.ActivePredictor, CensusOf(census[k]), Compute(matured[k])))
            .ToList();
    }

    /// <summary>
    /// Folds one group's census cells (one per state) into its GroupCensus. Ordinal state matching,
    /// mirroring ForecastAccuracyReadStore.GetCensusAsync: a mis-cased state lands in Total but in no
    /// bucket, a defect to be seen rather than absorbed. Only the PENDING cell's min harvest date is
    /// read — on terminal cells the column describes rows already resolved, which answer no "when can
    /// this be scored?" question. An empty cell set (a matured-only key the census missed) folds to
    /// all zeros and a null date.
    /// </summary>
    private static GroupCensus CensusOf(IEnumerable<ForecastSnapshotGroupCensusRow> cells)
    {
        var list = cells as IReadOnlyList<ForecastSnapshotGroupCensusRow> ?? cells.ToList();

        int Count(string state) => list
            .Where(c => string.Equals(c.MaturityState, state, StringComparison.Ordinal))
            .Sum(c => c.Count);

        // Returned AS-IS, never clamped to today: a harvest date already in the past means pending rows
        // are OVERDUE (waiting on a published price, or the maturity sweep has not run) — a signal the
        // page must show, not smooth over. Only a future date reads as "first score possible then".
        var earliestPending = list
            .Where(c => string.Equals(c.MaturityState, ForecastSnapshotMaturityStates.Pending, StringComparison.Ordinal))
            .Min(c => c.EarliestHarvestDate); // Min over none, or over nulls, is null — not a throw

        return new GroupCensus(
            Total: list.Sum(c => c.Count),
            Pending: Count(ForecastSnapshotMaturityStates.Pending),
            Matured: Count(ForecastSnapshotMaturityStates.Matured),
            ActualUnavailable: Count(ForecastSnapshotMaturityStates.ActualUnavailable),
            NotMaturable: Count(ForecastSnapshotMaturityStates.NotMaturable),
            EarliestScoreableHarvestDate: earliestPending);
    }

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

        decimal? mape = apes.Count == 0 ? null : Round(apes.Average(), MagnitudeDecimals);

        // The do-nothing baseline: score the carry-forward ReferencePrice as if IT were the prediction,
        // to the SAME convention as the stored PercentageError (snapshots.py _percentage_error:
        // error / max(|actual|, 1e-6) * 100 — percent units, absolute-value denominator, 1e-6 clip),
        // so BaselineMape and the model's error are directly comparable. The clip is mirrored here so
        // an actual of exactly zero (nothing in the DB forbids one) yields a huge-but-finite APE
        // instead of a DivideByZeroException taking down the whole endpoint. Computed over the scored
        // rows that also carry the two prices the formula needs; a scored row without a reference is
        // EXCLUDED and visible as the gap between ScoredCount and BaselineScoredCount, never treated
        // as zero-error.
        var baselineScored = scored
            .Where(r => r.ReferencePrice.HasValue && r.ActualPrice.HasValue)
            .ToList();
        var baselineApes = baselineScored
            .Select(r => Math.Abs(r.ReferencePrice!.Value - r.ActualPrice!.Value)
                / Math.Max(Math.Abs(r.ActualPrice!.Value), 0.000001m) * 100m)
            .ToList();

        decimal? baselineMape = baselineApes.Count == 0 ? null : Round(baselineApes.Average(), MagnitudeDecimals);

        // Skill compares like with like: the model's MAPE restricted to the SAME anchored rows the
        // baseline was scored on (mean of their stored PercentageError magnitudes, same rounding).
        // Skill is measured over the N = baselineScoredCount rows where both the prediction and the
        // do-nothing anchor are measurable; the headline Mape above keeps covering every scored row,
        // so the two may differ when some rows are anchorless.
        decimal? anchoredMape = baselineScored.Count == 0
            ? null
            : Round(baselineScored.Select(r => Math.Abs(r.PercentageError!.Value)).Average(), MagnitudeDecimals);

        // Ratio of the two ROUNDED figures. Null (not a blow-up, not 0) when nothing is anchored or
        // when BaselineMape is 0.00 — meaning the baseline mean rounds to zero at 2 dp, so the ratio
        // is unpublishable at page precision, NOT that the baseline was perfect.
        decimal? skillVsBaseline = anchoredMape is null || baselineMape is null || baselineMape.Value == 0m
            ? null
            : Round(anchoredMape.Value / baselineMape.Value, MagnitudeDecimals);

        // Of the ANCHORED rows, those whose prediction is value-equal to the anchor (decimal equality,
        // scale-insensitive; both prices are stored decimal(10,2), so equality is meaningful).
        // Anchorless rows are unmeasured, not non-copies — with no reference there is nothing to
        // compare the prediction against.
        var predictionEqualsReference = baselineScored.Count(r =>
            r.PredictedPrice == r.ReferencePrice!.Value);

        var intervalScored = list.Count(r => r.WithinInterval.HasValue);
        var withinInterval = list.Count(r => r.WithinInterval == true);

        decimal? coverage = intervalScored == 0
            ? null
            : Round((decimal)withinInterval / intervalScored, RateDecimals);

        var direction = Directional(list);

        return new AccuracyMetrics(
            MaturedCount: list.Count,
            ScoredCount: scored.Count,
            Mape: mape,
            MedianApe: apes.Count == 0 ? null : Round(Median(apes), MagnitudeDecimals),
            SignedBias: signedErrors.Count == 0 ? null : Round(signedErrors.Average(), MagnitudeDecimals),
            BaselineScoredCount: baselineScored.Count,
            BaselineMape: baselineMape,
            BaselineMedianApe: baselineApes.Count == 0 ? null : Round(Median(baselineApes), MagnitudeDecimals),
            SkillVsBaseline: skillVsBaseline,
            PredictionEqualsReferenceCount: predictionEqualsReference,
            PredictionEqualsReferenceShare: baselineScored.Count == 0
                ? null
                : Round((decimal)predictionEqualsReference / baselineScored.Count, RateDecimals),
            IntervalScoredCount: intervalScored,
            WithinIntervalCount: withinInterval,
            IntervalCoverage: coverage,
            IntervalCoverageGap: coverage is null ? null : Round(coverage.Value - NominalIntervalCoverage, RateDecimals),
            DirectionalAccuracy: direction.Accuracy,
            DirectionalScored: direction.Scored,
            DirectionalDegenerate: direction.Degenerate,
            DirectionalExcluded: direction.Excluded);
    }

    /// <summary>
    /// Share of rows where the forecast got the DIRECTION of the move right, measured from the price
    /// known on plant day: predicted = sign(PredictedPrice − ReferencePrice), actual =
    /// sign(ActualPrice − ReferencePrice), and a row counts as a hit when the two signs agree.
    /// </summary>
    /// <remarks>
    /// Every matured row lands in exactly ONE of three buckets:
    ///
    ///   EXCLUDED — NULL ReferencePrice (no carry-forward anchor existed at snapshot time) or NULL
    ///   ActualPrice. Not scored as misses: there is no direction to be right or wrong about.
    ///
    ///   DEGENERATE — assessable rows whose PredictedPrice is value-equal to ReferencePrice (the same
    ///   decimal-equality convention as PredictionEqualsReferenceCount). The predicted move is exactly
    ///   zero, so there is no nonzero direction to score. Counted, never scored — neither hit nor
    ///   miss. Equality alone cannot tell a fallback COPY of the anchor from a model that genuinely
    ///   forecasts "no change"; both land here. Under the old scoring, sign(0) == sign(0) made every
    ///   such row a "hit" whenever the actual price sat exactly flat, so a fallback that copies the
    ///   reference scored high by measuring price stasis, not model skill.
    ///
    ///   SCORED — the rest, scored sign-vs-sign as before. A scored row where the actual move is flat
    ///   but the predicted move is nonzero stays a MISS: the model claimed a move that didn't happen.
    ///
    /// Every bucket's count is returned so the figure is never read as covering more rows than it
    /// does. The Python evaluate.directional_accuracy contract {directional_acc, n_scored, n_excluded}
    /// deliberately differs from this in TWO ways: it has no degenerate bucket (deadband-0 scores
    /// sign 0 against sign 0 as a hit), and it scores a flat prediction against a moving actual as a
    /// MISS — its stated policy is "hedging is not rewarded". Here a flat prediction is not scored at
    /// all, so hedging is neither rewarded nor punished: the .NET summary is the admin-facing figure
    /// and must not count stasis as skill, at the accepted cost of not penalising flat forecasts.
    /// Accuracy is null (not 0, not 1) when nothing is scorable.
    /// </remarks>
    private static (decimal? Accuracy, int Scored, int Degenerate, int Excluded) Directional(
        IReadOnlyList<ForecastSnapshotScoringRow> rows)
    {
        var assessable = rows
            .Where(r => r.ReferencePrice.HasValue && r.ActualPrice.HasValue)
            .ToList();

        var excluded = rows.Count - assessable.Count;

        // Same value-equality convention as PredictionEqualsReferenceCount above: both prices are
        // stored decimal(10,2), so equality is meaningful. Known UNDER-detection: the two prices are
        // rounded by different code before storage (Python round-half-even for the prediction, SQL
        // decimal rounding for the raw reference), so a true carry-forward copy can arrive one cent
        // apart and slip into SCORED with a one-cent "move". Whether the copy test should tolerate
        // ±0.01 — here and in PredictionEqualsReferenceCount together — is an open follow-up
        // decision, not an accident.
        var scorable = assessable
            .Where(r => r.PredictedPrice != r.ReferencePrice!.Value)
            .ToList();
        var degenerate = assessable.Count - scorable.Count;

        if (scorable.Count == 0)
            return (null, 0, degenerate, excluded);

        // Deliberate: a nonzero predicted move against an exactly flat actual is a MISS — the model
        // claimed a move that didn't happen. (A zero predicted move cannot reach here; that is the
        // degenerate bucket.)
        var hits = scorable.Count(r =>
            Math.Sign(r.PredictedPrice - r.ReferencePrice!.Value) ==
            Math.Sign(r.ActualPrice!.Value - r.ReferencePrice!.Value));

        return (Round((decimal)hits / scorable.Count, RateDecimals), scorable.Count, degenerate, excluded);
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
