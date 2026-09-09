using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;

/// <summary>
/// The accuracy aggregation, kept as pure functions over already-fetched matured rows so it can be
/// tested without a database and cannot drift between the two callers.
/// </summary>
/// <remarks>
/// THE SPLIT LAW (PRD §3.4): a combined, model-and-fallback-blended accuracy number is prohibited. There
/// is no "overall" entry point here on purpose — EVERY public method that returns accuracy figures
/// returns them keyed by ActivePredictor (the horizon buckets and the worst-crops ranking included), so
/// the only way to produce a blended figure is to write new code to do it. A model that serves 11 crops
/// and a crop-mean fallback that serves 85 average out to a number that describes neither, and an admin
/// reading it would credit the model with the fallback's behaviour. The rule holds unqualified: an
/// earlier draft carved worst-crops out as "a different axis", and review proved the carve-out published
/// exactly the blended number the law forbids.
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

    /// <summary>
    /// THE MINIMUM-SAMPLE GATE (audit Critical-3): below this many scored rows, the magnitude and rate
    /// metrics are masked to null on the wire — the counts always survive, so the page reads
    /// "n=16, below the 30-row minimum" instead of a meaningless 4.92%. This deliberately nulls
    /// TODAY'S live headline MAPE (16 scored fallback rows as of the audit): that is the intended
    /// honesty — the same fix that stops a 1-row window happily printing "MAPE 0.00". The gate is a
    /// FINAL MASKING STEP over already-computed metrics (see WithMinimumSampleGate), never baked into
    /// the arithmetic, so the maths stays independently testable at any population size.
    /// </summary>
    public const int MinScoredCountForMetrics = 30;

    /// <summary>
    /// THE MACRO CROP BAR: the macro pair (MacroMape/MacroMedianApe) publishes only when BOTH the row
    /// gate above holds (ScoredCount >= MinScoredCountForMetrics — the rows feeding the per-crop
    /// figures must be adequate) AND DistinctCropCount reaches this many crops. A mean over fewer
    /// than 3 crops is not a "typical crop": at 1 crop it merely duplicates micro, and at 2 the
    /// failure case is concrete — 29 rows of one crop at APE 50 plus 1 row of another at APE 0 opens
    /// the row gate at scored 30 and publishes macroMape 25.00 with a SINGLE ROW carrying half the
    /// crop-weight. Applied in WithMinimumSampleGate as a CONSTANT, not a threshold seam:
    /// DistinctCropCount is a crop-mix fact, not a sample size the value tests need to dial down.
    /// </summary>
    public const int MinDistinctCropsForMacro = 3;

    /// <summary>
    /// A (predictor, crop) pair enters the worst-crops ranking only with at least this many NON-COPY
    /// scored rows (copies — predictions value-equal to the carry-forward anchor, see
    /// IsCopyOfReference — are not measured forecasts, so they neither qualify a crop nor move its
    /// figures): a crop scored once at APE 80% is an anecdote, not the worst crop, and a crop whose
    /// rows are ALL copies cannot be ranked at all — it has no measured forecasts. Sent on the wire so
    /// an empty list can say WHY it is empty rather than reading as "no crop is bad".
    ///
    /// Deliberately LOWER than MinScoredCountForMetrics (5 vs 30), one stated rationale: this list is
    /// ordinal TRIAGE with a per-entry disclosed n, not a published magnitude verdict. Entries below
    /// the metrics gate are FLAGGED (per-entry MeetsMinimumSample false), never hidden — hiding them
    /// would un-rank the very crops the list exists to surface.
    /// </summary>
    public const int WorstCropMinScoredCount = 5;

    /// <summary>
    /// Cap on the worst-crops list, PER PREDICTOR: each predictor contributes up to this many of its
    /// own worst entries, and the final list is the concatenation (re-sorted worst-first for
    /// display). A single global cap let one predictor starve the other out entirely — 15 bad
    /// fallback crops filled all the slots and the model's worst crops never appeared. Still a
    /// triage list, not a full per-crop report.
    /// </summary>
    public const int WorstCropsMaxEntries = 10;

    // The horizon-bucket BOUNDARY CONVENTION, exact and closed over the integers:
    //   short   = GrowthPeriodDays <  60
    //   medium  = 60 <= GrowthPeriodDays <= 120   (both boundary values are MEDIUM)
    //   long    = GrowthPeriodDays > 120
    //   unknown = GrowthPeriodDays is null — reachable only if the Python writer misbehaves (it mints
    //             'pending', the only state that can mature, only when a positive growth period
    //             resolved a harvest date; but no DB constraint enforces that, same gap as
    //             HarvestDate on the census row), so such rows get an explicit bucket and are NEVER
    //             silently dropped from the breakdown.
    // So gp=59 is short, gp=60 and gp=120 are medium, gp=121 is long.
    public const int ShortHorizonMaxGrowthPeriodDaysExclusive = 60;
    public const int MediumHorizonMaxGrowthPeriodDaysInclusive = 120;

    // The bucket labels as they appear on the wire — lowercase keys, not display text.
    public const string HorizonBucketShort = "short";
    public const string HorizonBucketMedium = "medium";
    public const string HorizonBucketLong = "long";
    public const string HorizonBucketUnknown = "unknown";

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
    ///
    /// Mape and MedianApe are MICRO-averages: every scored row weighs equally, so whichever crop has
    /// most rows dominates. MacroMape/MacroMedianApe are the MACRO companions over the same scored
    /// rows: each crop's own figure first, then the mean of those per-crop figures with equal CROP
    /// weight, over DistinctCropCount crops. Micro answers "how wrong is a typical prediction"; macro
    /// answers "how wrong is a typical crop"; the two diverging means a few heavy crops dominate the
    /// pooled figure (Simpson's-paradox alarm). DistinctCropCount is the macro denominator — and on
    /// the wire the macro pair has TWO publication bars, ROWS and CROPS: ScoredCount against the row
    /// gate AND DistinctCropCount against MinDistinctCropsForMacro (see WithMinimumSampleGate). This
    /// record itself is pre-gate and always carries the computed values.
    ///
    /// Min/MaxGrowthPeriodDays span the SCORED rows (null when none, or when every scored row's growth
    /// period is null). They make survivorship visible: a row only matures at SnapshotDate +
    /// GrowthPeriodDays, so a 30-day window's group showing max 45 is telling the admin that
    /// long-horizon crops CANNOT have matured into this window yet — the group's numbers describe
    /// fast-growing crops only, whatever the group's name suggests.
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
        int DirectionalExcluded,
        int DistinctCropCount,
        decimal? MacroMape,
        decimal? MacroMedianApe,
        int? MinGrowthPeriodDays,
        int? MaxGrowthPeriodDays);

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
    /// Metrics for the matured rows of one (ActivePredictor, horizon bucket) pair. The predictor is
    /// part of the key for the same split-law reason as ModelVersionGroup: a bucket pooled across
    /// predictors would blend model-served and fallback-served rows back into the number PRD §3.4
    /// forbids. HorizonBucket is one of the HorizonBucket* label constants.
    /// </summary>
    public sealed record HorizonBucketGroup(string ActivePredictor, string HorizonBucket, AccuracyMetrics Metrics);

    /// <summary>
    /// One (ActivePredictor, crop) entry in the worst-crops ranking. The predictor is part of the KEY,
    /// same split-law reason as every other group: a crop-only entry pooling its predictors' rows is
    /// the blended number PRD §3.4 forbids. MedianApe/Mape are that predictor's own figures for the
    /// crop over its NON-COPY scored rows (ScoredCount − CopyCount of them), 2 dp like every other
    /// magnitude metric; ScoredCount and CopyCount disclose how much was discounted.
    /// MeetsMinimumSample flags whether the non-copy population reaches the metrics gate
    /// (MinScoredCountForMetrics) — small-sample entries are badged, not hidden (see
    /// WorstCropMinScoredCount's rationale).
    /// </summary>
    public sealed record WorstCrop(
        string ActivePredictor,
        Guid CropId,
        string CropName,
        int ScoredCount,
        int CopyCount,
        decimal MedianApe,
        decimal Mape,
        bool MeetsMinimumSample);

    /// <summary>
    /// Groups the matured rows by (ActivePredictor, horizon bucket) — the boundary convention is the
    /// constants block above. Survivorship is exactly why this exists: /summary windows on
    /// SnapshotDate, but a row matures only at SnapshotDate + GrowthPeriodDays, so a short window can
    /// only ever contain fast-growing crops and the pooled figure is structurally weighted toward
    /// them; splitting by horizon puts that structure on the page instead of inside the average.
    /// </summary>
    /// <remarks>
    /// Every predictor with matured rows gets ALL THREE named buckets, empty ones included (zero
    /// counts, null metrics via the same empty-Compute path as everywhere else): an EMPTY long bucket
    /// in a 30-day window is the survivorship signal itself — "no long-horizon crop can have matured
    /// yet" — and dropping it would hide exactly the fact the breakdown exists to show. The "unknown"
    /// bucket (null growth period, a writer defect — see the constants block) appears ONLY when
    /// occupied: an always-present empty defect bucket would read as a normal category.
    ///
    /// Matured rows only, no census side: a pending-only predictor already exists on the summary via
    /// ByPredictor's census, and the census read carries no growth period to bucket by. Ordering is
    /// predictor (ordinal), then short/medium/long/unknown.
    /// </remarks>
    public static List<HorizonBucketGroup> ByHorizonBucket(IEnumerable<ForecastSnapshotScoringRow> maturedRows)
    {
        var bucketOrder = new[]
            { HorizonBucketShort, HorizonBucketMedium, HorizonBucketLong, HorizonBucketUnknown };

        return maturedRows
            .ToLookup(r => r.ActivePredictor, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .SelectMany(byPredictor =>
            {
                var byBucket = byPredictor.ToLookup(r => BucketOf(r.GrowthPeriodDays), StringComparer.Ordinal);
                return bucketOrder
                    .Where(b => b != HorizonBucketUnknown || byBucket[b].Any())
                    .Select(b => new HorizonBucketGroup(byPredictor.Key, b, Compute(byBucket[b])));
            })
            .ToList();
    }

    // The single point where a growth period becomes a bucket label; the boundary constants above are
    // the documentation of record.
    private static string BucketOf(int? growthPeriodDays) => growthPeriodDays switch
    {
        null => HorizonBucketUnknown,
        < ShortHorizonMaxGrowthPeriodDaysExclusive => HorizonBucketShort,
        <= MediumHorizonMaxGrowthPeriodDaysInclusive => HorizonBucketMedium,
        _ => HorizonBucketLong
    };

    /// <summary>
    /// THE ONE COPY TEST: the row's prediction made no claim independent of the carry-forward anchor —
    /// PredictedPrice is value-equal to ReferencePrice (decimal equality, scale-insensitive; both
    /// prices are stored decimal(10,2), so equality is meaningful). An anchorless row (null
    /// ReferencePrice) is NOT a copy — with no anchor there is nothing to have copied — but neither is
    /// it a measured non-copy for the metrics that need an anchor; each caller states which side it
    /// puts such rows on. Every copy-sensitive figure (PredictionEqualsReferenceCount, the directional
    /// degenerate bucket, the worst-crops non-copy filter) goes through THIS predicate, so the
    /// convention cannot fork. Known UNDER-detection: the two prices are rounded by different code
    /// before storage (Python round-half-even for the prediction, SQL decimal rounding for the raw
    /// reference), so a true carry-forward copy can arrive one cent apart and slip past. Whether the
    /// test should tolerate ±0.01 is an open follow-up decision, not an accident.
    /// </summary>
    public static bool IsCopyOfReference(ForecastSnapshotScoringRow row) =>
        row.ReferencePrice.HasValue && row.PredictedPrice == row.ReferencePrice.Value;

    /// <summary>
    /// Up to WorstCropsMaxEntries (ActivePredictor, crop) entries PER PREDICTOR, ranked by their own
    /// MedianApe, worst first, among pairs with at least
    /// <paramref name="minNonCopyScoredCountPerCrop"/> NON-COPY scored rows (the production value is
    /// WorstCropMinScoredCount; a parameter so the ranking is testable at any threshold).
    /// <paramref name="minScoredCountForMetrics"/> (production: MinScoredCountForMetrics via the
    /// handler's seam) only sets each entry's MeetsMinimumSample badge; it never filters. Empty when
    /// no pair qualifies — the caller publishes the threshold so the page can say why.
    /// </summary>
    /// <remarks>
    /// SPLIT LAW (PRD §3.4): every entry is keyed by (ActivePredictor, CropId) and its figures cover
    /// that predictor's rows ONLY. Ranking WITHIN the single list is fine — every entry names its
    /// predictor, so a reader can filter — but no entry ever pools two predictors' rows, and rows of
    /// different predictors never combine to reach the qualification minimum.
    ///
    /// COPY EXCLUSION: ranking statistics run over NON-COPY scored rows only (IsCopyOfReference; the
    /// same value-equality convention as A2's degenerate bucket). A copy has APE ≈ 0 by construction —
    /// it "predicts" the price already known on plant day — so including copies drags a crop's median
    /// toward zero and demotes exactly the crops farmers are most misled about (a fallback crop's 4
    /// copies once buried its 2 real misses at APE 90 below a steady crop at APE 6). An anchorless
    /// scored row counts as measured here: its stored error is a real forecast miss. A crop whose rows
    /// are all copies cannot be ranked — it has no measured forecasts — so a window where EVERYTHING is
    /// fallback copies yields an EMPTY list; with the thresholds and per-group copy counts on the wire,
    /// that emptiness is the honest answer, not a gap.
    ///
    /// THE CAP IS PER PREDICTOR: each predictor contributes up to WorstCropsMaxEntries of its OWN
    /// worst entries, and the final list is the concatenation, re-sorted once by the display
    /// ordering. A single global cap re-introduced starvation through the (predictor, crop) key: 15
    /// bad fallback crops filled all the slots and the model's worst crops never appeared — hiding
    /// exactly the per-predictor triage the split-keyed list exists to give.
    ///
    /// Ranked on MedianApe (the headline; robust to one spiked day) with Mape shown beside it; ties
    /// break by Mape descending, then crop name, crop id and predictor (ordinal) so the order is
    /// deterministic (RankWorstFirst — one comparer for the per-predictor cap and the display sort,
    /// so the two cannot diverge).
    /// </remarks>
    public static List<WorstCrop> WorstCropsByMedianApe(
        IEnumerable<ForecastSnapshotScoringRow> maturedRows,
        int minNonCopyScoredCountPerCrop,
        int minScoredCountForMetrics)
    {
        var qualified = maturedRows
            .Where(r => r.PercentageError.HasValue && r.SignedError.HasValue) // the shared scored filter
            .GroupBy(r => (r.ActivePredictor, r.CropId))
            .Select(g =>
            {
                var scoredCount = g.Count();
                var apes = g.Where(r => !IsCopyOfReference(r))
                    .Select(r => Math.Abs(r.PercentageError!.Value))
                    .ToList();
                return (Group: g, ScoredCount: scoredCount, NonCopyApes: apes);
            })
            // The > 0 leg is not redundant with the threshold: even a test threshold of 0 must not rank
            // an all-copy group — there is no median of zero measured forecasts to rank it BY.
            .Where(x => x.NonCopyApes.Count > 0 && x.NonCopyApes.Count >= minNonCopyScoredCountPerCrop)
            .Select(x => new WorstCrop(
                x.Group.Key.ActivePredictor,
                x.Group.Key.CropId,
                x.Group.First().CropName, // one crop, one name — every row carries the same joined value
                x.ScoredCount,
                x.ScoredCount - x.NonCopyApes.Count,
                Round(Median(x.NonCopyApes), MagnitudeDecimals),
                Round(x.NonCopyApes.Average(), MagnitudeDecimals),
                x.NonCopyApes.Count >= minScoredCountForMetrics));

        // Cap within each predictor's own ranking first (see the remarks), then one re-sort so the
        // wire carries a single deterministic worst-first list.
        return RankWorstFirst(qualified
                .GroupBy(c => c.ActivePredictor, StringComparer.Ordinal)
                .SelectMany(byPredictor => RankWorstFirst(byPredictor).Take(WorstCropsMaxEntries)))
            .ToList();
    }

    // The one worst-crops ordering, used both for the per-predictor cap and the final display sort:
    // MedianApe descending, ties by Mape descending, then crop name, crop id and predictor (ordinal).
    private static IOrderedEnumerable<WorstCrop> RankWorstFirst(IEnumerable<WorstCrop> entries) =>
        entries
            .OrderByDescending(c => c.MedianApe)
            .ThenByDescending(c => c.Mape)
            .ThenBy(c => c.CropName, StringComparer.Ordinal)
            .ThenBy(c => c.CropId)
            .ThenBy(c => c.ActivePredictor, StringComparer.Ordinal);

    /// <summary>
    /// The minimum-sample gate, as a FINAL masking step over already-computed metrics: each verdict
    /// metric is masked to null when the denominator PRINTED BESIDE IT is below
    /// <paramref name="minScoredCount"/> (production passes MinScoredCountForMetrics), and passes
    /// through untouched otherwise. One threshold, four denominators (the macro pair adds a second,
    /// crop-count bar):
    ///
    ///   ScoredCount          gates Mape, MedianApe, SignedBias
    ///   ScoredCount AND DistinctCropCount
    ///                        gate MacroMape, MacroMedianApe — TWO bars, rows and crops: the rows
    ///                        feeding the per-crop figures must be adequate (>= minScoredCount) AND
    ///                        there must be at least MinDistinctCropsForMacro crops to average (a
    ///                        constant, not the seam — see its rationale)
    ///   BaselineScoredCount  gates BaselineMape, BaselineMedianApe, SkillVsBaseline
    ///   DirectionalScored    gates DirectionalAccuracy
    ///   IntervalScoredCount  gates IntervalCoverage, IntervalCoverageGap
    ///
    /// PER-DENOMINATOR on purpose: keying everything on ScoredCount let a directionalAccuracy of 100%
    /// publish over directionalScored=2 (28 of the 30 scored rows were copies), a skill ratio publish
    /// over baselineScoredCount=3, and an intervalCoverage over 210 verdicts get masked because only
    /// 10 rows carried error columns. The page prints each rate next to its own n; the gate must key
    /// on that same n or the printed pair contradicts itself.
    ///
    /// Also returns the group's MeetsMinimumSample decision — defined as ScoredCount >=
    /// <paramref name="minScoredCount"/>, the HEADLINE population — as the single implementation of
    /// that invariant (callers must not re-derive it). Because the flag describes the headline
    /// denominator only, individual rates may be masked while it is true (few anchored/directional
    /// rows) or published while it is false (many interval verdicts): the flag and each rate answer
    /// for different denominators, by design.
    /// </summary>
    /// <remarks>
    /// What survives, deliberately: EVERY count (matured, scored, baseline-scored, copy, interval,
    /// directional scored/degenerate/excluded, distinct crops) — they are the disclosures that let the
    /// page say "n=16, below the 30-row minimum"; the Min/MaxGrowthPeriodDays disclosures — facts
    /// about which crops ARE in the window, not skill estimates; and PredictionEqualsReferenceShare —
    /// a statement about what the predictions ARE (copies of the anchor), which a small sample makes
    /// no less true and the page must still be able to show at n=16.
    /// </remarks>
    public static (AccuracyMetrics Metrics, bool MeetsMinimumSample) WithMinimumSampleGate(
        AccuracyMetrics metrics, int minScoredCount)
    {
        var scoredOk = metrics.ScoredCount >= minScoredCount;
        var baselineOk = metrics.BaselineScoredCount >= minScoredCount;
        var directionalOk = metrics.DirectionalScored >= minScoredCount;
        var intervalOk = metrics.IntervalScoredCount >= minScoredCount;
        // The macro pair's second bar: enough rows is not enough CROPS. A macro mean over fewer than
        // MinDistinctCropsForMacro crops is not a "typical crop" (at 1 crop it duplicates micro; at
        // 2, a single crop — even a single ROW — carries half the crop-weight), however many rows
        // fed it.
        var macroOk = scoredOk && metrics.DistinctCropCount >= MinDistinctCropsForMacro;

        var masked = metrics with
        {
            Mape = scoredOk ? metrics.Mape : null,
            MedianApe = scoredOk ? metrics.MedianApe : null,
            SignedBias = scoredOk ? metrics.SignedBias : null,
            MacroMape = macroOk ? metrics.MacroMape : null,
            MacroMedianApe = macroOk ? metrics.MacroMedianApe : null,
            BaselineMape = baselineOk ? metrics.BaselineMape : null,
            BaselineMedianApe = baselineOk ? metrics.BaselineMedianApe : null,
            SkillVsBaseline = baselineOk ? metrics.SkillVsBaseline : null,
            DirectionalAccuracy = directionalOk ? metrics.DirectionalAccuracy : null,
            IntervalCoverage = intervalOk ? metrics.IntervalCoverage : null,
            IntervalCoverageGap = intervalOk ? metrics.IntervalCoverageGap : null
        };

        return (masked, scoredOk);
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

        // Of the ANCHORED rows, the copies (IsCopyOfReference — the one shared convention).
        // Anchorless rows are unmeasured, not non-copies — with no reference there is nothing to
        // compare the prediction against — and the anchored filter above has already excluded them.
        var predictionEqualsReference = baselineScored.Count(IsCopyOfReference);

        // The macro companions to mape/medianApe, over the SAME scored rows: each crop's own figure
        // first (unrounded), then the mean of those per-crop figures with equal crop weight, rounded
        // once at the end — the same publish-once rounding as every other magnitude metric, so the
        // macro of one crop equals the micro exactly. distinctCropCount is the macro denominator.
        var perCropApes = scored
            .GroupBy(r => r.CropId)
            .Select(g => g.Select(r => Math.Abs(r.PercentageError!.Value)).ToList())
            .ToList();

        decimal? macroMape = perCropApes.Count == 0
            ? null
            : Round(perCropApes.Average(a => a.Average()), MagnitudeDecimals);
        decimal? macroMedianApe = perCropApes.Count == 0
            ? null
            : Round(perCropApes.Average(Median), MagnitudeDecimals);

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
            DirectionalExcluded: direction.Excluded,
            DistinctCropCount: perCropApes.Count,
            MacroMape: macroMape,
            MacroMedianApe: macroMedianApe,
            // Over the SCORED rows (the population the metrics describe). LINQ's nullable Min/Max is
            // null over an empty set and ignores null growth periods when real ones exist — so an
            // unknown-bucket row cannot poison the range, and an all-null group answers null, never 0.
            MinGrowthPeriodDays: scored.Min(r => r.GrowthPeriodDays),
            MaxGrowthPeriodDays: scored.Max(r => r.GrowthPeriodDays));
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

        // The shared copy convention (IsCopyOfReference — see its remarks for the known one-cent
        // under-detection): a copy slipping past lands in SCORED with a one-cent "move".
        var scorable = assessable
            .Where(r => !IsCopyOfReference(r))
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
