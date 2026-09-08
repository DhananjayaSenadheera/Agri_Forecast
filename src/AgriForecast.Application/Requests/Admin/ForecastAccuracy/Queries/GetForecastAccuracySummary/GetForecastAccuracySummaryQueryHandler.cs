using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// Three reads (the all-time state census, then the matured scoring rows and the per-group census, both
// inside the window) and the aggregation from ForecastAccuracyMath. The handler stays a mapper: all the
// maths is in that one tested class, and the DB is behind IForecastAccuracyReadStore.
//
// An empty table is a normal answer, not an error: zero counts, null latest date, and empty group lists.
public class GetForecastAccuracySummaryQueryHandler
    : IRequestHandler<GetForecastAccuracySummaryQuery, Result<ForecastAccuracySummary_GetDto>>
{
    private readonly IForecastAccuracyReadStore _store;

    public GetForecastAccuracySummaryQueryHandler(IForecastAccuracyReadStore store) => _store = store;

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
                .ToList()
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

    private static ForecastAccuracyMetrics_GetDto ToDto(ForecastAccuracyMath.AccuracyMetrics m) => new()
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
        DirectionalExcluded = m.DirectionalExcluded
    };

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Same stamp as the other admin reads: the wire must carry the trailing "Z" or the FE renders a UTC
    // instant as local time.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
