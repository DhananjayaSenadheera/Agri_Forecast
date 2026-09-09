using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// Three reads (the all-time state census, then the matured scoring rows and the per-group census, both
// inside the window) and the aggregation from ForecastAccuracyMath. The handler stays a mapper: all the
// maths is in that one tested class, and the DB is behind IForecastAccuracyReadStore. The horizon
// buckets, worst-crops list and crop-level macro figures all fold the SAME matured read in memory —
// still three queries total, never one per crop or bucket.
//
// An empty table is a normal answer, not an error: zero counts, null latest date, and empty group lists.
public class GetForecastAccuracySummaryQueryHandler
    : IRequestHandler<GetForecastAccuracySummaryQuery, Result<ForecastAccuracySummary_GetDto>>
{
    private readonly IForecastAccuracyReadStore _store;
    private readonly int _minScoredCountForMetrics;

    // The gate threshold is a constructor seam, NOT a query parameter: production always runs at the
    // constant (the DI container cannot resolve the int, so it takes the default — the documented
    // MS.DI behaviour for defaulted parameters), while the metric-value tests pass 0 to pin the
    // unmasked arithmetic on small seeded populations. A query parameter would let a caller switch
    // the honesty off over the wire.
    public GetForecastAccuracySummaryQueryHandler(
        IForecastAccuracyReadStore store,
        int minScoredCountForMetrics = ForecastAccuracyMath.MinScoredCountForMetrics)
    {
        _store = store;
        _minScoredCountForMetrics = minScoredCountForMetrics;
    }

    public async Task<Result<ForecastAccuracySummary_GetDto>> Handle(
        GetForecastAccuracySummaryQuery request, CancellationToken cancellationToken)
    {
        // The census is ALL-TIME on purpose (see ForecastAccuracySummary_GetDto.Counts); only the
        // scoring rows are windowed.
        var census = await _store.GetCensusAsync(cancellationToken);

        // SnapshotDate is a plain calendar date with no zone attached, so the cutoff is taken from UTC
        // today. At the ±5:30 Colombo offset that can move the boundary by one day either way, which is
        // immaterial for a window measured in hundreds of days and keeps the handler clock-free.
        var fromSnapshotDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-request.WindowDays);
        var matured = await _store.GetMaturedScoringRowsAsync(fromSnapshotDate, cancellationToken);

        // Same cutoff as the matured read, on purpose: the group census and the group metrics must
        // describe the same window. Within it, a group can have census rows but no matured rows yet —
        // that pending-only group appearing (instead of vanishing from a matured-only GroupBy) is the
        // entire point of the census.
        var groupCensus = await _store.GetGroupCensusAsync(fromSnapshotDate, cancellationToken);

        var dto = new ForecastAccuracySummary_GetDto
        {
            GeneratedAtUtc = AsUtc(DateTime.UtcNow),
            WindowDays = request.WindowDays,
            LatestSnapshotDate = census.LatestSnapshotDate.HasValue
                ? Fmt(census.LatestSnapshotDate.Value)
                : null,
            Counts = new ForecastSnapshotCounts_GetDto
            {
                Total = census.Total,
                Pending = census.Pending,
                Matured = census.Matured,
                ActualUnavailable = census.ActualUnavailable,
                NotMaturable = census.NotMaturable
            },
            ByActivePredictor = ForecastAccuracyMath.ByPredictor(matured, groupCensus)
                .Select(g => new PredictorAccuracy_GetDto
                {
                    ActivePredictor = g.ActivePredictor,
                    Census = ToDto(g.Census),
                    Metrics = ToDto(g.Metrics)
                })
                .ToList(),
            ByModelVersion = ForecastAccuracyMath.ByModelVersion(matured, groupCensus)
                .Select(g => new ModelVersionAccuracy_GetDto
                {
                    ModelVersion = g.ModelVersion,
                    ActivePredictor = g.ActivePredictor,
                    Census = ToDto(g.Census),
                    Metrics = ToDto(g.Metrics)
                })
                .ToList(),
            ByHorizonBucket = ForecastAccuracyMath.ByHorizonBucket(matured)
                .Select(g => new HorizonBucketAccuracy_GetDto
                {
                    ActivePredictor = g.ActivePredictor,
                    HorizonBucket = g.HorizonBucket,
                    Metrics = ToDto(g.Metrics)
                })
                .ToList(),
            // The metrics-gate threshold reaches the per-entry MeetsMinimumSample badge through the
            // same constructor seam the groups' gate uses, so the two honesty bars cannot diverge.
            WorstCrops = ForecastAccuracyMath
                .WorstCropsByMedianApe(matured, ForecastAccuracyMath.WorstCropMinScoredCount,
                    _minScoredCountForMetrics)
                .Select(c => new WorstCropAccuracy_GetDto
                {
                    ActivePredictor = c.ActivePredictor,
                    CropId = c.CropId,
                    CropName = c.CropName,
                    ScoredCount = c.ScoredCount,
                    CopyCount = c.CopyCount,
                    MedianApe = c.MedianApe,
                    Mape = c.Mape,
                    MeetsMinimumSample = c.MeetsMinimumSample
                })
                .ToList(),
            WorstCropMinScoredCount = ForecastAccuracyMath.WorstCropMinScoredCount
        };

        return Result<ForecastAccuracySummary_GetDto>.Success(dto);
    }

    private static ForecastSnapshotGroupCensus_GetDto ToDto(ForecastAccuracyMath.GroupCensus c) => new()
    {
        Total = c.Total,
        Pending = c.Pending,
        Matured = c.Matured,
        ActualUnavailable = c.ActualUnavailable,
        NotMaturable = c.NotMaturable,
        EarliestScoreableHarvestDate = c.EarliestScoreableHarvestDate.HasValue
            ? Fmt(c.EarliestScoreableHarvestDate.Value)
            : null
    };

    private ForecastAccuracyMetrics_GetDto ToDto(ForecastAccuracyMath.AccuracyMetrics unmasked)
    {
        // The gate is the LAST step before the wire, applied to fully-computed metrics: the arithmetic
        // never knows the threshold exists (which is what keeps it testable at any population size),
        // and everything below maps from the MASKED record so a gated group cannot leak a figure. The
        // gate also owns the MeetsMinimumSample decision — mapped, never re-derived here, so there is
        // exactly one implementation of the invariant.
        var (m, meetsMinimumSample) = ForecastAccuracyMath.WithMinimumSampleGate(unmasked, _minScoredCountForMetrics);

        return new ForecastAccuracyMetrics_GetDto
        {
            MaturedCount = m.MaturedCount,
            ScoredCount = m.ScoredCount,
            Mape = m.Mape,
            MedianApe = m.MedianApe,
            SignedBias = m.SignedBias,
            BaselineScoredCount = m.BaselineScoredCount,
            BaselineMape = m.BaselineMape,
            BaselineMedianApe = m.BaselineMedianApe,
            SkillVsBaseline = m.SkillVsBaseline,
            PredictionEqualsReferenceCount = m.PredictionEqualsReferenceCount,
            PredictionEqualsReferenceShare = m.PredictionEqualsReferenceShare,
            IntervalScoredCount = m.IntervalScoredCount,
            WithinIntervalCount = m.WithinIntervalCount,
            IntervalCoverage = m.IntervalCoverage,
            NominalIntervalCoverage = ForecastAccuracyMath.NominalIntervalCoverage,
            IntervalCoverageGap = m.IntervalCoverageGap,
            DirectionalAccuracy = m.DirectionalAccuracy,
            DirectionalScored = m.DirectionalScored,
            DirectionalDegenerate = m.DirectionalDegenerate,
            DirectionalExcluded = m.DirectionalExcluded,
            DistinctCropCount = m.DistinctCropCount,
            MacroMape = m.MacroMape,
            MacroMedianApe = m.MacroMedianApe,
            MinGrowthPeriodDays = m.MinGrowthPeriodDays,
            MaxGrowthPeriodDays = m.MaxGrowthPeriodDays,
            MeetsMinimumSample = meetsMinimumSample,
            MinScoredCountForMetrics = _minScoredCountForMetrics
        };
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Same stamp as the other admin reads: the wire must carry the trailing "Z" or the FE renders a UTC
    // instant as local time.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
