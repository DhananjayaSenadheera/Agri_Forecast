using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgriForecast.API.Controllers;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the admin forecast-accuracy read handlers, their validator and the aggregation maths.
/// The DB is faked via a canned IForecastAccuracyReadStore, so the aggregation, the model-vs-fallback
/// split, the per-version grouping, the paging/filtering contract and the UTC stamp all run in isolation.
/// The store's EF LINQ is not covered here (same boundary as the Logs-hub tests) — the group-census
/// query's translation is proven against a real database in ForecastAccuracyReadStoreTests; what IS
/// covered here is everything that decides what number an admin ends up reading.
/// </summary>
public class ForecastAccuracyHandlerTests
{
    // Fake read store: real in-memory census, ordering, paging and filtering over canned rows, plus a
    // record of what the handler actually asked for.
    private sealed class FakeStore : IForecastAccuracyReadStore
    {
        // Matured scoring rows paired with the SnapshotDate the real store filters them by (the scoring
        // projection itself does not carry the date — the window is applied in SQL).
        public List<(ForecastSnapshotScoringRow Row, DateOnly SnapshotDate)> Matured = new();
        public List<ForecastSnapshotListRow> Snapshots = new();
        public ForecastSnapshotCensus Census = new(0, 0, 0, 0, 0, null);

        // One fact per snapshot row for the GROUP census read: every state, with the SnapshotDate the
        // window filters on and the HarvestDate the pending-cells minimum comes from. AddMatured feeds
        // this list too, so the census and the matured read see one table, like the real store.
        public List<(string Predictor, string? ModelVersion, string State,
            DateOnly SnapshotDate, DateOnly? HarvestDate)> CensusFacts = new();

        public DateOnly? CapturedFromSnapshotDate;
        public DateOnly? CapturedCensusFromSnapshotDate;
        public Guid? CapturedCropId;
        public string? CapturedModelVersion;
        public bool? CapturedMaturedOnly;
        public int CapturedPage;
        public int CapturedPageSize;

        // Seeds a matured row inside the window (dated today), the common case for the metric tests.
        public void AddMatured(ForecastSnapshotScoringRow row, DateOnly? snapshotDate = null)
        {
            var date = snapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
            Matured.Add((row, date));
            // The matured census fact deliberately CARRIES a harvest date: only PENDING cells may feed
            // earliestScoreableHarvestDate, and a terminal cell with a date is what proves the
            // aggregation ignores it rather than never seeing one.
            AddCensusFact(row.ActivePredictor, row.ModelVersion,
                ForecastSnapshotMaturityStates.Matured, date, harvestDate: date);
        }

        // Seeds a census-only fact — a row in a state the matured scoring read never returns.
        public void AddCensusFact(string predictor, string? modelVersion, string state,
            DateOnly? snapshotDate = null, DateOnly? harvestDate = null)
            => CensusFacts.Add((predictor, modelVersion, state,
                snapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow), harvestDate));

        // Seeds a PENDING row. A real pending row always carries a harvest date (CreatePending
        // requires one), so it is non-optional here.
        public void AddPending(string predictor, string? modelVersion, DateOnly harvestDate,
            DateOnly? snapshotDate = null)
            => AddCensusFact(predictor, modelVersion, ForecastSnapshotMaturityStates.Pending,
                snapshotDate, harvestDate);

        public Task<ForecastSnapshotCensus> GetCensusAsync(CancellationToken ct = default)
            => Task.FromResult(Census);

        // Mirrors ForecastAccuracyReadStore.GetGroupCensusAsync's shape and window — the same cutoff
        // filter as the matured read, one cell per (predictor, version, state), MIN(HarvestDate) riding
        // along — EXCEPT collation: the real GROUP BY runs under the database's case-INSENSITIVE
        // collation for ActivePredictor/ModelVersion, while this GroupBy is ordinal ("V17" and "v17"
        // fuse there, stay apart here). MaturityState is safe on both sides: its BIN2 CHECK constraint
        // admits only the four lowercase spellings. Known and accepted — see the collation note in
        // ForecastAccuracyReadStore.GetSnapshotsPageAsync.
        public Task<IReadOnlyList<ForecastSnapshotGroupCensusRow>> GetGroupCensusAsync(
            DateOnly fromSnapshotDate, CancellationToken ct = default)
        {
            CapturedCensusFromSnapshotDate = fromSnapshotDate;
            return Task.FromResult<IReadOnlyList<ForecastSnapshotGroupCensusRow>>(
                CensusFacts
                    .Where(f => f.SnapshotDate >= fromSnapshotDate)
                    .GroupBy(f => (f.Predictor, f.ModelVersion, f.State))
                    .Select(g => new ForecastSnapshotGroupCensusRow(
                        g.Key.Predictor, g.Key.ModelVersion, g.Key.State, g.Count(),
                        g.Min(f => f.HarvestDate)))
                    .ToList());
        }

        public Task<IReadOnlyList<ForecastSnapshotScoringRow>> GetMaturedScoringRowsAsync(
            DateOnly fromSnapshotDate, CancellationToken ct = default)
        {
            CapturedFromSnapshotDate = fromSnapshotDate;
            return Task.FromResult<IReadOnlyList<ForecastSnapshotScoringRow>>(
                Matured.Where(m => m.SnapshotDate >= fromSnapshotDate).Select(m => m.Row).ToList());
        }

        public Task<ForecastSnapshotsPage> GetSnapshotsPageAsync(
            int page, int pageSize, Guid? cropId, string? modelVersion, bool maturedOnly,
            CancellationToken ct = default)
        {
            CapturedPage = page;
            CapturedPageSize = pageSize;
            CapturedCropId = cropId;
            CapturedModelVersion = modelVersion;
            CapturedMaturedOnly = maturedOnly;

            var filtered = Snapshots
                .Where(s => cropId is null || s.CropId == cropId)
                .Where(s => modelVersion is null || s.ModelVersion == modelVersion)
                .Where(s => !maturedOnly || s.MaturityState == ForecastSnapshotMaturityStates.Matured)
                .OrderByDescending(s => s.SnapshotDate)
                .ThenByDescending(s => s.Id)
                .ToList();

            return Task.FromResult(new ForecastSnapshotsPage(
                filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), filtered.Count));
        }
    }

    private static GetForecastAccuracySummaryQueryHandler SummaryHandler(FakeStore s) => new(s);
    private static GetForecastSnapshotsQueryHandler SnapshotsHandler(FakeStore s) => new(s);

    private const string Model = "residual";
    private const string Fallback = "crop_mean_fallback";

    // One matured scoring row. The error columns are given explicitly, exactly as the maturing pass
    // freezes them, because that is what the handler must read rather than re-derive.
    private static ForecastSnapshotScoringRow SRow(
        string predictor = Model,
        string? modelVersion = "v17",
        decimal? percentageError = 10m,
        decimal? signedError = 5m,
        bool? withinInterval = true,
        decimal predictedPrice = 105m,
        decimal? actualPrice = 100m,
        decimal? referencePrice = 90m)
        => new(predictor, modelVersion, predictedPrice, actualPrice, referencePrice,
            signedError, percentageError, withinInterval);

    private static ForecastSnapshotListRow LRow(
        DateOnly snapshotDate,
        Guid? id = null,
        Guid? cropId = null,
        string maturityState = ForecastSnapshotMaturityStates.Pending,
        string? modelVersion = "v17",
        string confidence = "High",
        string predictor = Model,
        DateOnly? harvestDate = null,
        decimal? actualPrice = null,
        DateTime? maturedAtUtc = null)
        => new(
            id ?? Guid.NewGuid(),
            cropId ?? Guid.NewGuid(),
            "Carrot",
            "VEG000001",
            snapshotDate,
            harvestDate,
            harvestDate is null ? null : 90,
            120.50m, 100.00m, 140.00m, 118.00m,
            confidence, predictor, null, modelVersion, "ok",
            maturityState,
            actualPrice,
            actualPrice is null ? null : harvestDate,
            actualPrice is null ? null : 120.50m - actualPrice,
            actualPrice is null ? null : Math.Abs(120.50m - actualPrice.Value),
            actualPrice is null ? null : 2.5000m,
            actualPrice is null ? null : true,
            new DateTime(2026, 7, 1, 21, 5, 0, DateTimeKind.Unspecified),
            maturedAtUtc);

    private static ForecastAccuracyMetrics_GetDto MetricsFor(
        ForecastAccuracySummary_GetDto dto, string predictor) =>
        dto.ByActivePredictor.Single(g => g.ActivePredictor == predictor).Metrics;

    // ---------------------------------------------------------------- SUMMARY: empty and counts

    // An admin opening the page before anything has matured must get zeros and nulls, not an error and
    // not 0.0 dressed up as "0% error".
    [Fact]
    public async Task Summary_EmptyStore_ReturnsZeroCountsAndEmptyGroups_NotAnError()
    {
        var result = await SummaryHandler(new FakeStore()).Handle(new GetForecastAccuracySummaryQuery(), default);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data;
        dto.Counts.Total.Should().Be(0);
        dto.Counts.Pending.Should().Be(0);
        dto.Counts.Matured.Should().Be(0);
        dto.LatestSnapshotDate.Should().BeNull();
        dto.ByActivePredictor.Should().BeEmpty();
        dto.ByModelVersion.Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_CountsAndLatestDate_AreMappedPerMaturityState()
    {
        var store = new FakeStore
        {
            Census = new ForecastSnapshotCensus(
                Total: 412, Pending: 300, Matured: 100, ActualUnavailable: 8, NotMaturable: 4,
                LatestSnapshotDate: new DateOnly(2026, 7, 26))
        };

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.Counts.Total.Should().Be(412);
        dto.Counts.Pending.Should().Be(300);           // the OPEN count
        dto.Counts.Matured.Should().Be(100);
        dto.Counts.ActualUnavailable.Should().Be(8);   // surfaced, never silently dropped
        dto.Counts.NotMaturable.Should().Be(4);
        dto.LatestSnapshotDate.Should().Be("2026-07-26");
    }

    // The wire needs the trailing "Z" or the FE renders a UTC instant as local.
    [Fact]
    public async Task Summary_GeneratedAt_IsUtcStamped()
    {
        var dto = (await SummaryHandler(new FakeStore()).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.GeneratedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        JsonSerializer.Serialize(dto.GeneratedAtUtc).Should().EndWith("Z\"");
    }

    // ---------------------------------------------------------------- SUMMARY: the split law

    // THE HARD LAW (PRD §3.4). A model serving a handful of crops well and a fallback serving most of
    // them badly must never average into one number: seed a 4% model and a 40% fallback and prove both
    // survive separately and that the 22% blend appears nowhere in the response.
    [Fact]
    public async Task Summary_ModelAndFallback_AreSplit_AndNoBlendedNumberExists()
    {
        var store = new FakeStore();
        for (var i = 0; i < 4; i++)
            store.AddMatured(SRow(predictor: Model, percentageError: 4m));
        for (var i = 0; i < 4; i++)
            store.AddMatured(SRow(predictor: Fallback, percentageError: 40m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByActivePredictor.Should().HaveCount(2);
        MetricsFor(dto, Model).Mape.Should().Be(4.00m);
        MetricsFor(dto, Model).MaturedCount.Should().Be(4);
        MetricsFor(dto, Fallback).Mape.Should().Be(40.00m);
        MetricsFor(dto, Fallback).MaturedCount.Should().Be(4);

        // The blend — MAPE 22.00 over all 8 rows — must not be reachable anywhere on the wire. Only the
        // aggregate groups are serialized here; generatedAtUtc is a timestamp and would match digits by
        // coincidence.
        var everyMetric = dto.ByActivePredictor.Select(g => g.Metrics)
            .Concat(dto.ByModelVersion.Select(g => g.Metrics))
            .ToList();
        everyMetric.Should().NotContain(m => m.Mape == 22.00m);
        everyMetric.Should().NotContain(m => m.MedianApe == 22.00m);
        everyMetric.Should().NotContain(m => m.MaturedCount == 8); // no group covers all 8 rows

        JsonSerializer.Serialize(new { dto.ByActivePredictor, dto.ByModelVersion })
            .Should().NotContain("22");
    }

    // Structural half of the same law: there is nowhere on the response to PUT a blended number. Metrics
    // only ever hang off an object whose key names the predictor.
    [Fact]
    public void Summary_Dto_HasNoTopLevelMetricsProperty()
    {
        var metricsProps = typeof(ForecastAccuracySummary_GetDto)
            .GetProperties()
            .Where(p => p.PropertyType == typeof(ForecastAccuracyMetrics_GetDto))
            .ToList();

        metricsProps.Should().BeEmpty(
            "accuracy exists only inside a predictor-keyed group; a top-level metrics object would be a blended number");

        // And every group type that carries metrics carries the predictor key with it.
        typeof(PredictorAccuracy_GetDto).GetProperty(nameof(PredictorAccuracy_GetDto.ActivePredictor))
            .Should().NotBeNull();
        typeof(ModelVersionAccuracy_GetDto).GetProperty(nameof(ModelVersionAccuracy_GetDto.ActivePredictor))
            .Should().NotBeNull();
    }

    // Per-version grouping keeps the predictor in the key, so a version that mostly fell back cannot
    // read as a version that was mostly right.
    [Fact]
    public async Task Summary_ByModelVersion_IsKeyedByVersionAndPredictor()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictor: Model, modelVersion: "v16", percentageError: 8m));
        store.AddMatured(SRow(predictor: Model, modelVersion: "v17", percentageError: 4m));
        store.AddMatured(SRow(predictor: Fallback, modelVersion: "v17", percentageError: 40m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByModelVersion.Should().HaveCount(3);
        dto.ByModelVersion.Single(g => g.ModelVersion == "v16" && g.ActivePredictor == Model)
            .Metrics.Mape.Should().Be(8.00m);
        dto.ByModelVersion.Single(g => g.ModelVersion == "v17" && g.ActivePredictor == Model)
            .Metrics.Mape.Should().Be(4.00m);
        dto.ByModelVersion.Single(g => g.ModelVersion == "v17" && g.ActivePredictor == Fallback)
            .Metrics.Mape.Should().Be(40.00m);

        // No v17 group blends the two predictors together.
        dto.ByModelVersion.Where(g => g.ModelVersion == "v17").Should().HaveCount(2);
    }

    // Rows served before a version was recorded keep their own group rather than being folded into a
    // version's numbers, and sort last.
    [Fact]
    public async Task Summary_NullModelVersion_IsItsOwnGroup_SortedLast()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(modelVersion: null, percentageError: 30m));
        store.AddMatured(SRow(modelVersion: "v17", percentageError: 4m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByModelVersion.Should().HaveCount(2);
        dto.ByModelVersion.Last().ModelVersion.Should().BeNull();
        dto.ByModelVersion.Last().Metrics.Mape.Should().Be(30.00m);
    }

    // ---------------------------------------------------------------- SUMMARY: the metrics themselves

    // medianAPE is the headline precisely because one spike must not move it the way it moves MAPE.
    [Fact]
    public async Task Summary_MedianApe_IsRobustToAnOutlier_WhereMapeIsNot()
    {
        var store = new FakeStore();
        foreach (var ape in new[] { 5m, 6m, 7m, 8m, 200m })
            store.AddMatured(SRow(percentageError: ape));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.Mape.Should().Be(45.20m);     // (5+6+7+8+200)/5
        m.MedianApe.Should().Be(7.00m); // the typical miss, unmoved by the spike
        m.ScoredCount.Should().Be(5);
    }

    // APE is the MAGNITUDE of the stored signed percentage error: a −12% miss is a 12% miss.
    [Fact]
    public async Task Summary_Ape_IsTheAbsoluteValueOfTheStoredSignedPercentageError()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: -12m));
        store.AddMatured(SRow(percentageError: 12m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.Mape.Should().Be(12.00m);
        m.MedianApe.Should().Be(12.00m);
    }

    // Even-count median = mean of the two middle values.
    [Fact]
    public async Task Summary_MedianApe_EvenCount_AveragesTheTwoMiddleValues()
    {
        var store = new FakeStore();
        foreach (var ape in new[] { 2m, 4m, 6m, 10m })
            store.AddMatured(SRow(percentageError: ape));

        MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model)
            .MedianApe.Should().Be(5.00m);
    }

    // signedBias keeps its sign: equal and opposite misses cancelling to ~0 is the fact being reported,
    // and a positive bias means the forecasts run high (over-promising the farmer).
    [Fact]
    public async Task Summary_SignedBias_KeepsItsSign_AndCancellationIsReportedNotHidden()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictor: Model, signedError: 10m));
        store.AddMatured(SRow(predictor: Model, signedError: -10m));
        store.AddMatured(SRow(predictor: Fallback, signedError: -8m));
        store.AddMatured(SRow(predictor: Fallback, signedError: -4m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        MetricsFor(dto, Model).SignedBias.Should().Be(0.00m);       // cancels — and says so
        MetricsFor(dto, Fallback).SignedBias.Should().Be(-6.00m);   // runs low
    }

    // Coverage is reported with the nominal 0.80 it is measured against, plus the signed gap.
    [Fact]
    public async Task Summary_IntervalCoverage_IsReportedAgainstNominalEighty()
    {
        var store = new FakeStore();
        for (var i = 0; i < 3; i++) store.AddMatured(SRow(withinInterval: true));
        for (var i = 0; i < 2; i++) store.AddMatured(SRow(withinInterval: false));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.IntervalScoredCount.Should().Be(5);
        m.WithinIntervalCount.Should().Be(3);
        m.IntervalCoverage.Should().Be(0.6000m);
        m.NominalIntervalCoverage.Should().Be(0.80m);
        m.IntervalCoverageGap.Should().Be(-0.2000m); // negative = overconfident band
        ForecastAccuracyMath.NominalIntervalCoverage.Should().Be(0.80m);
    }

    // A matured row whose error column the maturing pass left null is EXCLUDED from the metric, not
    // counted as a zero-error success. The gap between maturedCount and scoredCount makes it visible.
    [Fact]
    public async Task Summary_MaturedRowWithNullErrorColumns_IsExcluded_NotCountedAsZeroError()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: 20m, withinInterval: true));
        store.AddMatured(SRow(percentageError: null, signedError: null, withinInterval: null));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(2);
        m.ScoredCount.Should().Be(1);
        m.Mape.Should().Be(20.00m); // NOT 10 — the null row is not a zero
        m.IntervalScoredCount.Should().Be(1);
        m.IntervalCoverage.Should().Be(1.0000m);
    }

    // The three error metrics share ONE denominator by construction: a row missing EITHER error column
    // is excluded from mape, medianApe AND signedBias, so scoredCount describes all three. Without the
    // shared filter, signedBias would be averaged over 2 rows while the count beside it said 1.
    [Theory]
    [InlineData(true)]  // percentageError present, signedError missing
    [InlineData(false)] // signedError present, percentageError missing
    public async Task Summary_RowMissingEitherErrorColumn_IsExcludedFromAllThreeMetrics(bool signedIsNull)
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: 20m, signedError: 6m));
        store.AddMatured(signedIsNull
            ? SRow(percentageError: 100m, signedError: null)
            : SRow(percentageError: null, signedError: 60m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(2);
        m.ScoredCount.Should().Be(1);   // the honest denominator for all three below
        m.Mape.Should().Be(20.00m);     // the half-row never reaches any of them
        m.MedianApe.Should().Be(20.00m);
        m.SignedBias.Should().Be(6.00m);
    }

    // A group with nothing measurable reports nulls and zero counts, never 0.0.
    [Fact]
    public async Task Summary_GroupWithNoUsableColumns_ReportsNulls_NotZeroes()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: null, signedError: null, withinInterval: null,
            referencePrice: null, actualPrice: null));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(1);
        m.Mape.Should().BeNull();
        m.MedianApe.Should().BeNull();
        m.SignedBias.Should().BeNull();
        m.IntervalCoverage.Should().BeNull();
        m.IntervalCoverageGap.Should().BeNull();
        m.DirectionalAccuracy.Should().BeNull();
        m.DirectionalDegenerate.Should().Be(0); // unassessable is EXCLUDED, not a copy-claim
        m.DirectionalExcluded.Should().Be(1);
    }

    // ---------------------------------------------------------------- SUMMARY: the do-nothing baseline

    // THE LIVE FAILURE MODE this baseline exists to expose: every prediction is value-equal to the
    // carry-forward referencePrice, so the group's MAPE *is* the baseline's MAPE wearing a model's
    // name. skillVsBaseline must read exactly 1.00 and predictionEqualsReferenceShare exactly 1.0.
    [Fact]
    public async Task Summary_AllPredictionsCopyTheReference_SkillIsOne_AndTheCopyShareSaysSo()
    {
        var store = new FakeStore();
        // pred == ref on both rows; stored PE matches (pred − actual)/actual: 5% and −10%.
        store.AddMatured(SRow(predictedPrice: 105m, referencePrice: 105m, actualPrice: 100m,
            percentageError: 5m, signedError: 5m));
        store.AddMatured(SRow(predictedPrice: 90m, referencePrice: 90m, actualPrice: 100m,
            percentageError: -10m, signedError: -10m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.Mape.Should().Be(7.50m);                          // (5+10)/2
        m.BaselineScoredCount.Should().Be(2);
        m.BaselineMape.Should().Be(7.50m);                  // identical by construction
        m.BaselineMedianApe.Should().Be(7.50m);
        m.SkillVsBaseline.Should().Be(1.00m);               // no skill over doing nothing
        m.PredictionEqualsReferenceCount.Should().Be(2);
        m.PredictionEqualsReferenceShare.Should().Be(1.0000m);
    }

    // A model genuinely closer to the actual than the plant-day price: skill lands below 1.0.
    [Fact]
    public async Task Summary_ModelBeatsBaseline_SkillBelowOne()
    {
        var store = new FakeStore();
        // APE 2 vs baseline |90−100|/100·100 = 10
        store.AddMatured(SRow(predictedPrice: 102m, referencePrice: 90m, actualPrice: 100m,
            percentageError: 2m, signedError: 2m));
        // APE 3 vs baseline |110−100|/100·100 = 10
        store.AddMatured(SRow(predictedPrice: 97m, referencePrice: 110m, actualPrice: 100m,
            percentageError: -3m, signedError: -3m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.Mape.Should().Be(2.50m);
        m.BaselineMape.Should().Be(10.00m);
        m.BaselineMedianApe.Should().Be(10.00m);
        m.SkillVsBaseline.Should().Be(0.25m);               // 2.50 / 10.00
        m.PredictionEqualsReferenceCount.Should().Be(0);
        m.PredictionEqualsReferenceShare.Should().Be(0.0000m);
    }

    // A model further from the actual than the price already known on plant day: skill above 1.0, and
    // the ratio is rounded AwayFromZero from the two 2-dp figures (10.00/3.00 → 3.33).
    [Fact]
    public async Task Summary_ModelLosesToBaseline_SkillAboveOne()
    {
        var store = new FakeStore();
        // APE 10 vs baseline |103−100|/100·100 = 3
        store.AddMatured(SRow(predictedPrice: 110m, referencePrice: 103m, actualPrice: 100m,
            percentageError: 10m, signedError: 10m));
        // APE 10 vs baseline |97−100|/100·100 = 3
        store.AddMatured(SRow(predictedPrice: 90m, referencePrice: 97m, actualPrice: 100m,
            percentageError: -10m, signedError: -10m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.Mape.Should().Be(10.00m);
        m.BaselineMape.Should().Be(3.00m);
        m.SkillVsBaseline.Should().Be(3.33m);               // 10.00/3.00 = 3.333… → 3.33
    }

    // A scored row with no plant-day anchor is EXCLUDED from the baseline (visible via the count), and
    // from BOTH sides of the skill ratio: the anchorless row's APE (25) is deliberately different from
    // the anchored row's (5), so a skill computed from the headline mape (15.00/5.00 = 3.00) would fail
    // here. It is also excluded from the copy share — unmeasured, not a non-copy.
    [Fact]
    public async Task Summary_NullReferenceRow_LeavesTheBaseline_TheSkillRatio_AndTheCopyShare()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 125m, referencePrice: null, actualPrice: 100m,
            percentageError: 25m, signedError: 25m));
        store.AddMatured(SRow(predictedPrice: 105m, referencePrice: 105m, actualPrice: 100m,
            percentageError: 5m, signedError: 5m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(2);
        m.Mape.Should().Be(15.00m);                         // headline still covers ALL scored rows
        m.BaselineScoredCount.Should().Be(1);               // the anchorless row is out, and it shows
        m.BaselineMape.Should().Be(5.00m);                  // over 1 row, NOT diluted over 2
        m.SkillVsBaseline.Should().Be(1.00m);               // anchored-rows MAPE 5.00 / 5.00, NOT 15.00/5.00
        m.PredictionEqualsReferenceCount.Should().Be(1);
        m.PredictionEqualsReferenceShare.Should().Be(1.0000m); // 1 of 1 ANCHORED rows, not 1 of 2 scored
    }

    // A baselineMape of 0.00 means the baseline mean rounds to zero at the page's 2 dp: the ratio is
    // unpublishable at page precision, so skill is null — not an exception and not infinity dressed as
    // a number.
    [Fact]
    public async Task Summary_BaselineMapeZero_SkillIsNull_NotADivisionBlowUp()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, referencePrice: 100m, actualPrice: 100m,
            percentageError: 5m, signedError: 5m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.BaselineScoredCount.Should().Be(1);
        m.BaselineMape.Should().Be(0.00m);
        m.BaselineMedianApe.Should().Be(0.00m);
        m.SkillVsBaseline.Should().BeNull();
    }

    // When NO scored row carries an anchor, every anchored metric is null with a zero count: baseline,
    // skill AND the copy share. The share in particular must be null (unmeasured), not 0.0000 — a zero
    // would read as "no prediction ever copied the reference" when in fact nothing could be measured.
    [Fact]
    public async Task Summary_AllScoredRowsAnchorless_EveryAnchoredMetricIsNull_WithZeroCounts()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, referencePrice: null, actualPrice: 100m,
            percentageError: 5m, signedError: 5m));
        store.AddMatured(SRow(predictedPrice: 112m, referencePrice: null, actualPrice: 100m,
            percentageError: 12m, signedError: 12m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(2);
        m.Mape.Should().Be(8.50m);                          // the model is still measurable
        m.BaselineScoredCount.Should().Be(0);
        m.BaselineMape.Should().BeNull();
        m.BaselineMedianApe.Should().BeNull();
        m.SkillVsBaseline.Should().BeNull();
        m.PredictionEqualsReferenceCount.Should().Be(0);
        m.PredictionEqualsReferenceShare.Should().BeNull();
    }

    // An actual of exactly zero (nothing in the DB forbids one) must not 500 the endpoint with a
    // DivideByZeroException: the baseline mirrors the Python 1e-6 denominator clip, so the APE comes
    // out huge but finite — |50 − 0| / 1e-6 · 100 = 5e9 percent. Pinned on the FALLBACK group so at
    // least one baseline test proves the maths is predictor-agnostic.
    [Fact]
    public async Task Summary_ActualPriceZero_BaselineIsHugeButFinite_NotADivideByZero()
    {
        var store = new FakeStore();
        // Defensive only — a real row like this cannot be stored: PercentageError is decimal(9,4)
        // (max 99999.9999) and Python's _accepted_actual refuses a non-positive actual upstream.
        // The fixture pins that IF such a row ever appeared, the clip yields huge-but-finite, not a 500.
        store.AddMatured(SRow(predictor: Fallback, predictedPrice: 50m, referencePrice: 50m,
            actualPrice: 0m, percentageError: 5000000000m, signedError: 50m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Fallback);

        m.BaselineScoredCount.Should().Be(1);
        m.BaselineMape.Should().Be(5000000000.00m);
        m.BaselineMedianApe.Should().Be(5000000000.00m);
        m.SkillVsBaseline.Should().Be(1.00m);
        m.PredictionEqualsReferenceCount.Should().Be(1);
        m.PredictionEqualsReferenceShare.Should().Be(1.0000m);
    }

    // The baseline hangs off the SAME scored filter as the error metrics: a matured row without error
    // columns reaches none of the new fields, even when it carries both prices. Nothing scored ⇒ nulls
    // and zero counts, never 0.0.
    [Fact]
    public async Task Summary_NoScoredRows_BaselineFieldsAreNullsAndZeroCounts()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: null, signedError: null, withinInterval: null,
            referencePrice: null, actualPrice: null));
        // Unscored but price-complete, pred == ref: still invisible to every baseline field.
        store.AddMatured(SRow(percentageError: null, signedError: null, withinInterval: null,
            predictedPrice: 90m, referencePrice: 90m, actualPrice: 100m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(2);
        m.ScoredCount.Should().Be(0);
        m.BaselineScoredCount.Should().Be(0);
        m.BaselineMape.Should().BeNull();
        m.BaselineMedianApe.Should().BeNull();
        m.SkillVsBaseline.Should().BeNull();
        m.PredictionEqualsReferenceCount.Should().Be(0);
        m.PredictionEqualsReferenceShare.Should().BeNull();
        // The two "copy" counts run over DIFFERENT populations, pinned here: the pred==ref row has no
        // error columns, so it is invisible to predictionEqualsReferenceCount (anchored rows only) —
        // yet it IS price-complete, so the directional partition counts it as degenerate.
        m.DirectionalDegenerate.Should().Be(1);
        m.DirectionalExcluded.Should().Be(1);
    }

    // ---------------------------------------------------------------- SUMMARY: directional accuracy

    // Direction is measured from the plant-day reference: predicted up + actual up = a hit.
    [Fact]
    public async Task Summary_DirectionalAccuracy_ScoresAgainstTheStoredReferencePrice()
    {
        var store = new FakeStore();
        // hit: predicted up (105 > 90), actual up (100 > 90)
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 100m, referencePrice: 90m));
        // hit: predicted down, actual down
        store.AddMatured(SRow(predictedPrice: 80m, actualPrice: 85m, referencePrice: 90m));
        // miss: predicted up, actual down
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 85m, referencePrice: 90m));
        // miss: predicted down, actual up
        store.AddMatured(SRow(predictedPrice: 80m, actualPrice: 95m, referencePrice: 90m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.DirectionalScored.Should().Be(4);
        m.DirectionalDegenerate.Should().Be(0);
        m.DirectionalExcluded.Should().Be(0);
        m.DirectionalAccuracy.Should().Be(0.5000m);
    }

    // Rows with no plant-day anchor have no direction to be right or wrong about: EXCLUDED, and the
    // exclusion is counted so the figure is never read as covering more rows than it does.
    [Fact]
    public async Task Summary_DirectionalAccuracy_NullReferenceRows_AreExcluded_NotScoredAsMisses()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 100m, referencePrice: 90m)); // hit
        store.AddMatured(SRow(referencePrice: null));  // no anchor
        store.AddMatured(SRow(actualPrice: null));     // never scored against an actual
        // Bucket precedence, pinned: pred == ref but no actual — assessability is tested FIRST, so
        // this row is EXCLUDED; the degenerate test never sees it.
        store.AddMatured(SRow(predictedPrice: 90m, referencePrice: 90m, actualPrice: null));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(4);
        m.DirectionalScored.Should().Be(1);
        m.DirectionalDegenerate.Should().Be(0); // no anchor to be equal TO — excluded, not degenerate
        m.DirectionalExcluded.Should().Be(3);
        m.DirectionalAccuracy.Should().Be(1.0000m); // 1/1, NOT 1/3
    }

    // THE PROPERTY the degenerate bucket exists to guarantee: a copy-prediction (pred value-equal to
    // the reference, same convention as predictionEqualsReferenceCount) must NEVER move
    // directionalAccuracy — whatever its actual price does. The flat-actual case is the one that used
    // to score a free "hit" (sign 0 == sign 0), which is how 16 copy rows once read as 87.5%: the
    // figure measured price stasis, not model skill.
    [Theory]
    [InlineData(90)]  // actual flat — the previously-inflating case
    [InlineData(95)]  // actual up
    [InlineData(85)]  // actual down
    public async Task Summary_CopyPredictionRow_NeverMovesDirectionalAccuracy_WhateverTheActualDoes(
        int actualPrice)
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 100m, referencePrice: 90m)); // hit
        store.AddMatured(SRow(predictedPrice: 80m, actualPrice: 95m, referencePrice: 90m));   // miss
        // The copy row: no directional opinion, counted but never scored — neither hit nor miss.
        store.AddMatured(SRow(predictedPrice: 90m, actualPrice: actualPrice, referencePrice: 90m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.DirectionalScored.Should().Be(2);
        m.DirectionalDegenerate.Should().Be(1);
        m.DirectionalExcluded.Should().Be(0);
        m.DirectionalAccuracy.Should().Be(0.5000m); // 1/2 with or without the copy row
    }

    // TODAY'S LIVE SITUATION: every matured row is a fallback copy of its reference. The old scoring
    // read this as 87.5% "accuracy"; the honest answer is null — nothing scorable — with the
    // degenerate count carrying the whole population so the page can say WHY there is no figure.
    [Fact]
    public async Task Summary_AllPredictionsCopyTheReference_DirectionalAccuracyIsNull_AndTheDegenerateCountSaysSo()
    {
        var store = new FakeStore();
        for (var i = 0; i < 16; i++)
            store.AddMatured(SRow(predictor: Fallback, predictedPrice: 90m, actualPrice: 90m,
                referencePrice: 90m, percentageError: 0m, signedError: 0m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Fallback);

        m.MaturedCount.Should().Be(16);
        m.DirectionalScored.Should().Be(0);
        m.DirectionalDegenerate.Should().Be(16);
        m.DirectionalExcluded.Should().Be(0);
        m.DirectionalAccuracy.Should().BeNull(); // NOT 0 (nothing missed) and NOT 1 (nothing hit)
    }

    // Mixed population: the figure is computed over the real predictions only, and the three buckets
    // partition the matured rows exactly — scored + degenerate + excluded = maturedCount.
    [Fact]
    public async Task Summary_CopiesAndRealPredictionsMixed_AccuracyCoversTheRealOnesOnly()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 100m, referencePrice: 90m)); // hit
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 85m, referencePrice: 90m));  // miss
        store.AddMatured(SRow(predictedPrice: 90m, actualPrice: 90m, referencePrice: 90m));   // copy
        store.AddMatured(SRow(predictedPrice: 90m, actualPrice: 95m, referencePrice: 90m));   // copy
        store.AddMatured(SRow(referencePrice: null));                                         // no anchor

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(5);
        m.DirectionalScored.Should().Be(2);
        m.DirectionalDegenerate.Should().Be(2);
        m.DirectionalExcluded.Should().Be(1);
        m.DirectionalAccuracy.Should().Be(0.5000m); // 1/2, NOT 3/4 (copies not hits) and NOT 1/5
    }

    // Deliberate asymmetry: a REAL prediction against an exactly flat actual is a miss — the model
    // claimed a move that didn't happen. Only the zero-claim (copy) rows are exempt from scoring.
    [Fact]
    public async Task Summary_FlatActual_WithANonzeroPrediction_StaysAMiss()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 105m, actualPrice: 90m, referencePrice: 90m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.DirectionalScored.Should().Be(1);
        m.DirectionalDegenerate.Should().Be(0);
        m.DirectionalAccuracy.Should().Be(0.0000m); // a measured miss, which is a different fact from null
    }

    // ---------------------------------------------------------------- SUMMARY: the window

    // The window bounds the METRICS only. A row that aged out still exists in the ledger census — the
    // asymmetry is deliberate, and this test is what stops someone "fixing" it by windowing the counts.
    [Fact]
    public async Task Summary_RowsOutsideTheWindow_LeaveTheMetrics_ButStayInTheCounts()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var store = new FakeStore
        {
            Census = new ForecastSnapshotCensus(
                Total: 2, Pending: 0, Matured: 2, ActualUnavailable: 0, NotMaturable: 0,
                LatestSnapshotDate: today)
        };
        store.AddMatured(SRow(percentageError: 5m), today.AddDays(-10));   // inside
        store.AddMatured(SRow(percentageError: 95m), today.AddDays(-400)); // aged out

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        MetricsFor(dto, Model).MaturedCount.Should().Be(1);
        MetricsFor(dto, Model).Mape.Should().Be(5.00m); // NOT 50 — the old row is out of the window
        dto.Counts.Matured.Should().Be(2);              // but still counted in the ledger census
    }

    [Fact]
    public async Task Summary_DefaultWindow_IsOneYear_AndIsEchoedBack()
    {
        var store = new FakeStore();

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WindowDays.Should().Be(365);
        GetForecastAccuracySummaryQuery.DefaultWindowDays.Should().Be(365);
        store.CapturedFromSnapshotDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-365));
    }

    // An explicit window is passed to the store as a cutoff date and echoed on the response, so a number
    // is never shown without the span it covers.
    [Fact]
    public async Task Summary_ExplicitWindow_BoundsTheStoreReadAndIsEchoedBack()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: 5m), today.AddDays(-3));
        store.AddMatured(SRow(percentageError: 95m), today.AddDays(-40));

        var dto = (await SummaryHandler(store).Handle(
            new GetForecastAccuracySummaryQuery { WindowDays = 30 }, default)).Data;

        dto.WindowDays.Should().Be(30);
        store.CapturedFromSnapshotDate.Should().Be(today.AddDays(-30));
        MetricsFor(dto, Model).Mape.Should().Be(5.00m);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(365, true)]
    [InlineData(3650, true)]
    [InlineData(3651, false)]
    public void SummaryValidator_BoundsTheWindow(int windowDays, bool expectedValid)
    {
        new GetForecastAccuracySummaryValidator()
            .Validate(new GetForecastAccuracySummaryQuery { WindowDays = windowDays })
            .IsValid.Should().Be(expectedValid);
    }

    // ---------------------------------------------------------------- SUMMARY: census-based groups

    // THE LIVE SHAPE the group census exists for (audit High-2): a fallback with a few matured rows and
    // an ML model whose hundreds of rows are ALL still pending. The old matured-only GroupBy erased the
    // model from the summary entirely, so an admin could not tell "no data yet" from "no such model".
    // The model group must exist, say plainly that nothing is scored yet (maturedCount 0, null metrics
    // — never fabricated zeros), and say when the first real score becomes POSSIBLE.
    [Fact]
    public async Task Summary_PendingOnlyGroup_Appears_WithNullMetricsAndTheDateAScoreBecomesPossible()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictor: Fallback, modelVersion: null, percentageError: 5m));
        store.AddPending(Model, "v16", harvestDate: new DateOnly(2026, 9, 20));
        store.AddPending(Model, "v16", harvestDate: new DateOnly(2026, 9, 15)); // the earliest
        store.AddPending(Model, "v16", harvestDate: new DateOnly(2026, 11, 2));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByActivePredictor.Should().HaveCount(2); // the model is PRESENT despite zero matured rows

        var model = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model);
        model.Census.Total.Should().Be(3);
        model.Census.Pending.Should().Be(3);
        model.Census.Matured.Should().Be(0);
        model.Census.ActualUnavailable.Should().Be(0);
        model.Census.NotMaturable.Should().Be(0);
        model.Census.EarliestScoreableHarvestDate.Should().Be("2026-09-15"); // min PENDING harvest date
        model.Metrics.MaturedCount.Should().Be(0);
        model.Metrics.ScoredCount.Should().Be(0);
        model.Metrics.Mape.Should().BeNull();       // not yet scored — null, never 0.0 dressed up
        model.Metrics.MedianApe.Should().BeNull();
        model.Metrics.SignedBias.Should().BeNull();
        model.Metrics.SkillVsBaseline.Should().BeNull();
        model.Metrics.IntervalCoverage.Should().BeNull();
        model.Metrics.DirectionalAccuracy.Should().BeNull();

        // The same group exists on the per-version list, still keyed by predictor.
        var version = dto.ByModelVersion.Single(g => g.ModelVersion == "v16" && g.ActivePredictor == Model);
        version.Census.Pending.Should().Be(3);
        version.Census.EarliestScoreableHarvestDate.Should().Be("2026-09-15");
        version.Metrics.MaturedCount.Should().Be(0);
        version.Metrics.Mape.Should().BeNull();

        // And the fallback's matured-row metrics are exactly what they were before the census existed.
        var fallback = dto.ByActivePredictor.Single(g => g.ActivePredictor == Fallback);
        fallback.Census.Matured.Should().Be(1);
        fallback.Census.Pending.Should().Be(0);
        fallback.Metrics.MaturedCount.Should().Be(1);
        fallback.Metrics.Mape.Should().Be(5.00m);
    }

    // A group with nothing in flight has no forthcoming score, so the date is NULL — not a date scraped
    // off its terminal rows. The fixture plants harvest dates on matured AND actual_unavailable cells
    // precisely so a leak from a terminal cell would be caught, not silently plausible.
    [Fact]
    public async Task Summary_GroupWithNoPendingRows_HasNullEarliestScoreableHarvestDate()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: 5m)); // matured census fact carries a harvest date
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.ActualUnavailable,
            harvestDate: new DateOnly(2020, 1, 1)); // earlier than anything — must still not surface

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var model = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model);
        model.Census.Pending.Should().Be(0);
        model.Census.EarliestScoreableHarvestDate.Should().BeNull();
    }

    // The date is NOT forward-looking and must never be clamped to today. A pending row is scored on
    // harvest day only if a price published that exact day; otherwise it sits pending through the
    // maturity grace window — and if the nightly sweep stops running, it sits there indefinitely. So a
    // PAST earliestScoreableHarvestDate is a routine, load-bearing signal ("pending rows are overdue —
    // waiting on a published price, or the sweep is stuck"), and clamping it away would dress that
    // warning up as a promise.
    [Fact]
    public async Task Summary_EarliestScoreableHarvestDate_PastDateMeansOverduePendingRows_NotClamped()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var store = new FakeStore();
        store.AddPending(Model, "v16", harvestDate: today.AddDays(-21)); // harvested weeks ago, still unpriced
        store.AddPending(Model, "v16", harvestDate: today.AddDays(30));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var model = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model);
        model.Census.Pending.Should().Be(2);
        model.Census.EarliestScoreableHarvestDate.Should()
            .Be(today.AddDays(-21).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "the past date is the overdue signal — returned as-is, never clamped to today");
    }

    // Reachable only if the writer misbehaves — serving/snapshots.py assigns pending only when a
    // harvest date exists — but no DB constraint ties state to HarvestDate, so the maths must answer
    // null, not throw, when every pending cell's date is null. Seeded via AddCensusFact directly:
    // AddPending (like the writer) refuses to model a dateless pending row.
    [Fact]
    public async Task Summary_AllPendingHarvestDatesNull_EarliestScoreableHarvestDateIsNull()
    {
        var store = new FakeStore();
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.Pending, harvestDate: null);
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.Pending, harvestDate: null);

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var model = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model);
        model.Census.Pending.Should().Be(2);
        model.Census.EarliestScoreableHarvestDate.Should().BeNull(
            "Min over all-null dates is null — the rows still count as pending, but no date can be named");
    }

    // Every lifecycle state is counted, and Total is summed independently of the four buckets (same
    // arithmetic-gap defence as the top-level counts).
    [Fact]
    public async Task Summary_GroupCensus_CountsEveryLifecycleState()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(percentageError: 5m));
        store.AddPending(Model, "v17", harvestDate: new DateOnly(2026, 10, 1));
        store.AddPending(Model, "v17", harvestDate: new DateOnly(2026, 10, 8));
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.ActualUnavailable,
            harvestDate: new DateOnly(2026, 8, 1));
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.ActualUnavailable,
            harvestDate: new DateOnly(2026, 8, 2));
        store.AddCensusFact(Model, "v17", ForecastSnapshotMaturityStates.NotMaturable); // no harvest date, ever

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var census = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model).Census;
        census.Total.Should().Be(6);
        census.Pending.Should().Be(2);
        census.Matured.Should().Be(1);
        census.ActualUnavailable.Should().Be(2);
        census.NotMaturable.Should().Be(1);
        census.EarliestScoreableHarvestDate.Should().Be("2026-10-01"); // from the PENDING rows only
    }

    // The arithmetic-gap defence, end to end: a mis-cased state (the DB's BIN2 CHECK should make one
    // impossible, which is exactly when a silent absorb would go unnoticed) lands in the group's Total
    // but in NO named bucket — total > sum of buckets, a visible defect. This is the test that stops
    // someone "simplifying" Total into the sum of the four buckets. Ordinal matching also means the
    // mis-cased cell is NOT the pending bucket, so its harvest date must not surface either.
    [Fact]
    public async Task Summary_MisCasedState_LandsInTotalButNoBucket_SoTheGapIsVisible()
    {
        var store = new FakeStore();
        store.AddPending(Model, "v17", harvestDate: new DateOnly(2026, 10, 1));
        store.AddCensusFact(Model, "v17", "Pending", harvestDate: new DateOnly(2026, 1, 1)); // mis-cased

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var census = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model).Census;
        census.Total.Should().Be(2);
        (census.Pending + census.Matured + census.ActualUnavailable + census.NotMaturable)
            .Should().Be(1, "the mis-cased row is in Total and in no bucket — the gap IS the alarm");
        census.Pending.Should().Be(1);
        census.EarliestScoreableHarvestDate.Should().Be("2026-10-01",
            "\"Pending\" is not pending: its earlier date must not masquerade as the scoreable date");
    }

    // CensusOf's only non-trivial arithmetic, exercised across more than one cell: ByPredictor folds
    // EVERY version's cells of one predictor into a single census — counts SUM across versions per
    // state, and the earliest scoreable date is the MIN across BOTH versions' pending cells — while
    // ByModelVersion keeps the very same cells apart, each version with its own min.
    [Fact]
    public async Task Summary_ByPredictor_FoldsAllVersionsOfAPredictor_SummingStatesAndTakingTheMinPendingDate()
    {
        var store = new FakeStore();
        // v16: two pending (the later dates) + one matured + one actual_unavailable.
        store.AddPending(Model, "v16", harvestDate: new DateOnly(2026, 10, 1));
        store.AddPending(Model, "v16", harvestDate: new DateOnly(2026, 12, 1));
        store.AddMatured(SRow(modelVersion: "v16", percentageError: 8m));
        store.AddCensusFact(Model, "v16", ForecastSnapshotMaturityStates.ActualUnavailable,
            harvestDate: new DateOnly(2026, 7, 1)); // terminal cell's date — must not win the min
        // v17: the earliest pending date of all + one matured.
        store.AddPending(Model, "v17", harvestDate: new DateOnly(2026, 9, 15));
        store.AddMatured(SRow(modelVersion: "v17", percentageError: 4m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var model = dto.ByActivePredictor.Single(g => g.ActivePredictor == Model);
        model.Census.Total.Should().Be(6);
        model.Census.Pending.Should().Be(3);           // 2 (v16) + 1 (v17)
        model.Census.Matured.Should().Be(2);           // 1 + 1 across versions
        model.Census.ActualUnavailable.Should().Be(1);
        model.Census.NotMaturable.Should().Be(0);
        model.Census.EarliestScoreableHarvestDate.Should().Be("2026-09-15",
            "the min runs across BOTH versions' pending cells, not the first cell encountered");

        // The same cells, per version: each keeps its own count and its own min.
        var v16 = dto.ByModelVersion.Single(g => g.ModelVersion == "v16" && g.ActivePredictor == Model);
        v16.Census.Pending.Should().Be(2);
        v16.Census.EarliestScoreableHarvestDate.Should().Be("2026-10-01");
        var v17 = dto.ByModelVersion.Single(g => g.ModelVersion == "v17" && g.ActivePredictor == Model);
        v17.Census.Pending.Should().Be(1);
        v17.Census.EarliestScoreableHarvestDate.Should().Be("2026-09-15");
    }

    // The census respects windowDays exactly as the metrics do — ONE cutoff date reaches both store
    // reads — so a pending row that aged out neither keeps its group alive nor drags the
    // earliest-scoreable date backwards. Within the window, a group with census rows but no matured
    // rows is the expected shape, not an inconsistency.
    [Fact]
    public async Task Summary_GroupCensus_RespectsTheSameWindowAsTheMetrics()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var store = new FakeStore();
        // Model: an aged-out pending row with the EARLIER harvest date, one pending row inside.
        store.AddPending(Model, "v16", harvestDate: today.AddDays(5), snapshotDate: today.AddDays(-400));
        store.AddPending(Model, "v16", harvestDate: today.AddDays(60), snapshotDate: today.AddDays(-3));
        // Fallback: ONLY aged-out rows — no group at all inside the window.
        store.AddPending(Fallback, null, harvestDate: today.AddDays(10), snapshotDate: today.AddDays(-400));

        var dto = (await SummaryHandler(store).Handle(
            new GetForecastAccuracySummaryQuery { WindowDays = 30 }, default)).Data;

        store.CapturedCensusFromSnapshotDate.Should().Be(today.AddDays(-30));
        store.CapturedCensusFromSnapshotDate.Should().Be(store.CapturedFromSnapshotDate); // one window, both reads

        var model = dto.ByActivePredictor.Should().ContainSingle().Subject;
        model.ActivePredictor.Should().Be(Model);
        model.Census.Pending.Should().Be(1); // the aged-out row left the census too
        model.Census.EarliestScoreableHarvestDate.Should()
            .Be(today.AddDays(60).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    // The group keys are the UNION of the census and matured reads: a matured key the census read
    // returned no cells for still gets its group — with an all-zero census — rather than vanishing
    // from the page. No RACE can produce this shape (the matured rows are read FIRST, nothing deletes
    // snapshot rows, and maturing never rewrites a row's predictor or version; a row maturing between
    // the reads only puts census.matured one above maturedCount), so the fixture fakes the divergence
    // directly. In reality it would take casing/whitespace divergence between the ordinal C# grouping
    // and the SQL GROUP BY's case-insensitive collation — the axis named on the FakeStore comment
    // above. A cheap defence against key-set divergence between two independent reads, kept.
    [Fact]
    public async Task Summary_MaturedRowMissingFromTheCensusRead_StillFormsItsGroup()
    {
        var store = new FakeStore();
        // Straight into the matured list, deliberately bypassing AddMatured's census fact.
        store.Matured.Add((SRow(percentageError: 5m), DateOnly.FromDateTime(DateTime.UtcNow)));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var g = dto.ByActivePredictor.Should().ContainSingle().Subject;
        g.Metrics.MaturedCount.Should().Be(1);
        g.Metrics.Mape.Should().Be(5.00m);
        g.Census.Total.Should().Be(0); // the census read had no cells for this key — visible divergence, not a lost group
    }

    // ---------------------------------------------------------------- SNAPSHOTS: paging and filters

    [Fact]
    public async Task Snapshots_PagingMath_ReturnsRequestedPageAndTotal()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Snapshots.Add(LRow(new DateOnly(2026, 7, 20).AddDays(i)));

        var dto = (await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { Page = 2, PageSize = 2 }, default)).Data;

        dto.Total.Should().Be(5);
        dto.Page.Should().Be(2);
        dto.PageSize.Should().Be(2);
        dto.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Snapshots_NewestSnapshotDateFirst()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 20)));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26)));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 24)));

        var dto = (await SnapshotsHandler(store).Handle(new GetForecastSnapshotsQuery(), default)).Data;

        dto.Items.Select(i => i.SnapshotDate).Should()
            .ContainInOrder("2026-07-26", "2026-07-24", "2026-07-20");
    }

    // A page past the end (and a filter that matches nothing) is a success with empty items, never a
    // failure the FE would have to render as an error.
    [Fact]
    public async Task Snapshots_PagePastTheEnd_IsSuccessWithEmptyItems()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26)));

        var result = await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { Page = 9, PageSize = 20 }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Items.Should().BeEmpty();
        result.Data.Total.Should().Be(1);
    }

    [Fact]
    public async Task Snapshots_EmptyStore_IsSuccessWithEmptyItems()
    {
        var result = await SnapshotsHandler(new FakeStore()).Handle(new GetForecastSnapshotsQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Items.Should().BeEmpty();
        result.Data.Total.Should().Be(0);
    }

    [Fact]
    public async Task Snapshots_CropIdFilter_NarrowsToThatCrop()
    {
        var store = new FakeStore();
        var wanted = Guid.NewGuid();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), cropId: wanted));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26)));

        var dto = (await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { CropId = wanted }, default)).Data;

        store.CapturedCropId.Should().Be(wanted);
        dto.Items.Should().ContainSingle().Which.CropId.Should().Be(wanted);
    }

    [Fact]
    public async Task Snapshots_ModelVersionFilter_NarrowsToThatVersion()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), modelVersion: "v17"));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), modelVersion: "v16"));

        var dto = (await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { ModelVersion = " v17 " }, default)).Data;

        store.CapturedModelVersion.Should().Be("v17"); // trimmed
        dto.Items.Should().ContainSingle().Which.ModelVersion.Should().Be("v17");
    }

    // A blank ?modelVersion= is NO filter — filtering on the empty string would hand the admin an empty
    // page for what looks like an unfiltered request.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task Snapshots_BlankModelVersion_IsNoFilter(string? modelVersion)
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), modelVersion: "v17"));

        var dto = (await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { ModelVersion = modelVersion }, default)).Data;

        store.CapturedModelVersion.Should().BeNull();
        dto.Items.Should().HaveCount(1);
    }

    // maturedOnly means the MATURED state alone, not "anything terminal": actual_unavailable and
    // not_maturable rows are terminal too and carry no error columns.
    [Fact]
    public async Task Snapshots_MaturedOnly_ExcludesEveryOtherTerminalState()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), maturityState: ForecastSnapshotMaturityStates.Matured,
            harvestDate: new DateOnly(2026, 7, 20), actualPrice: 118m));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), maturityState: ForecastSnapshotMaturityStates.Pending));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), maturityState: ForecastSnapshotMaturityStates.ActualUnavailable));
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), maturityState: ForecastSnapshotMaturityStates.NotMaturable));

        var dto = (await SnapshotsHandler(store).Handle(
            new GetForecastSnapshotsQuery { MaturedOnly = true }, default)).Data;

        store.CapturedMaturedOnly.Should().BeTrue();
        dto.Items.Should().ContainSingle()
            .Which.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Matured);
    }

    // ---------------------------------------------------------------- SNAPSHOTS: row mapping

    [Fact]
    public async Task Snapshots_Row_IsMappedVerbatim_WithUtcStampsAndYmdDates()
    {
        var store = new FakeStore();
        var maturedAt = new DateTime(2026, 7, 27, 21, 30, 0, DateTimeKind.Unspecified);
        store.Snapshots.Add(LRow(
            new DateOnly(2026, 5, 1),
            maturityState: ForecastSnapshotMaturityStates.Matured,
            confidence: "Low",                       // a fallback-served row
            predictor: Fallback,
            harvestDate: new DateOnly(2026, 7, 30),
            actualPrice: 118.00m,
            maturedAtUtc: maturedAt));

        var item = (await SnapshotsHandler(store).Handle(new GetForecastSnapshotsQuery(), default)).Data.Items.Single();

        item.SnapshotDate.Should().Be("2026-05-01");
        item.HarvestDate.Should().Be("2026-07-30");
        item.ActualObservedDate.Should().Be("2026-07-30");
        item.GrowthPeriodDays.Should().Be(90);
        item.CropName.Should().Be("Carrot");
        item.CropCode.Should().Be("VEG000001");
        item.PredictedPrice.Should().Be(120.50m);
        item.LowerBound.Should().Be(100.00m);
        item.UpperBound.Should().Be(140.00m);
        item.ReferencePrice.Should().Be(118.00m);
        item.ActualPrice.Should().Be(118.00m);
        item.PercentageError.Should().Be(2.5000m); // read as stored, not recomputed
        item.WithinInterval.Should().BeTrue();
        item.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        item.MaturedAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);

        // A Low-confidence fallback stays Low on the way out.
        item.Confidence.Should().Be("Low");
        item.ActivePredictor.Should().Be(Fallback);
    }

    // A pending row has no harvest-side data yet; nulls stay null rather than being faked.
    [Fact]
    public async Task Snapshots_PendingRow_LeavesActualAndErrorColumnsNull()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), harvestDate: new DateOnly(2026, 10, 24)));

        var item = (await SnapshotsHandler(store).Handle(new GetForecastSnapshotsQuery(), default)).Data.Items.Single();

        item.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Pending);
        item.ActualPrice.Should().BeNull();
        item.ActualObservedDate.Should().BeNull();
        item.SignedError.Should().BeNull();
        item.PercentageError.Should().BeNull();
        item.WithinInterval.Should().BeNull();
        item.MaturedAtUtc.Should().BeNull();
    }

    // A not_maturable row has no harvest date at all.
    [Fact]
    public async Task Snapshots_NotMaturableRow_HasNullHarvestDate()
    {
        var store = new FakeStore();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26),
            maturityState: ForecastSnapshotMaturityStates.NotMaturable));

        var item = (await SnapshotsHandler(store).Handle(new GetForecastSnapshotsQuery(), default)).Data.Items.Single();

        item.HarvestDate.Should().BeNull();
        item.GrowthPeriodDays.Should().BeNull();
    }

    // ---------------------------------------------------------------- VALIDATOR

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(1, -5)]
    public void Validator_RejectsOutOfBoundsPaging(int page, int pageSize)
    {
        new GetForecastSnapshotsValidator()
            .Validate(new GetForecastSnapshotsQuery { Page = page, PageSize = pageSize })
            .IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 100)]
    [InlineData(500, 20)]
    public void Validator_AcceptsBoundsAndBeyond(int page, int pageSize)
    {
        new GetForecastSnapshotsValidator()
            .Validate(new GetForecastSnapshotsQuery { Page = page, PageSize = pageSize })
            .IsValid.Should().BeTrue();
    }

    // An all-zeroes GUID is a client bug, not a legitimate "no rows" query.
    [Fact]
    public void Validator_RejectsEmptyCropId_ButAllowsNoCropId()
    {
        var v = new GetForecastSnapshotsValidator();

        var rejected = v.Validate(new GetForecastSnapshotsQuery { CropId = Guid.Empty });
        rejected.IsValid.Should().BeFalse();

        // The 400 body keys the error under the parameter the caller actually sent, not under the
        // nullable-unwrapping this validator does internally.
        rejected.Errors.Should().ContainSingle()
            .Which.PropertyName.Should().Be(nameof(GetForecastSnapshotsQuery.CropId));

        v.Validate(new GetForecastSnapshotsQuery { CropId = Guid.NewGuid() }).IsValid.Should().BeTrue();
        v.Validate(new GetForecastSnapshotsQuery { CropId = null }).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validator_RejectsOverlongModelVersion()
    {
        var v = new GetForecastSnapshotsValidator();

        v.Validate(new GetForecastSnapshotsQuery { ModelVersion = new string('v', 21) })
            .IsValid.Should().BeFalse();
        v.Validate(new GetForecastSnapshotsQuery { ModelVersion = "v17" }).IsValid.Should().BeTrue();
        v.Validate(new GetForecastSnapshotsQuery { ModelVersion = "  " }).IsValid.Should().BeTrue();
    }

    // ---------------------------------------------------------------- CONTROLLER SEAM

    // The query-string parameters must reach the store: a maturedOnly=true that silently bound to false
    // would show an admin unmatured rows under a "scored" filter.
    [Fact]
    public async Task Controller_SnapshotsQueryString_BindsAndReachesTheStore()
    {
        var store = new FakeStore();
        var cropId = Guid.NewGuid();
        store.Snapshots.Add(LRow(new DateOnly(2026, 7, 26), cropId: cropId,
            maturityState: ForecastSnapshotMaturityStates.Matured, modelVersion: "v17",
            harvestDate: new DateOnly(2026, 7, 20), actualPrice: 118m));

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<GetForecastSnapshotsQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetForecastSnapshotsQuery q, CancellationToken ct) =>
                new GetForecastSnapshotsQueryHandler(store).Handle(q, ct));

        var controller = new AdminForecastAccuracyController(mediator.Object);

        var response = await controller.GetSnapshots(
            page: 1, pageSize: 50, cropId: cropId, modelVersion: "v17", maturedOnly: true);

        var ok = Assert.IsType<OkObjectResult>(response);
        var page = Assert.IsType<ForecastSnapshotsPage_GetDto>(ok.Value);
        page.Items.Should().ContainSingle();
        store.CapturedPage.Should().Be(1);
        store.CapturedPageSize.Should().Be(50);
        store.CapturedCropId.Should().Be(cropId);
        store.CapturedModelVersion.Should().Be("v17");
        store.CapturedMaturedOnly.Should().BeTrue();
    }

    [Fact]
    public async Task Controller_Summary_ReturnsTheSplitAggregates()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictor: Model, percentageError: 4m));
        store.AddMatured(SRow(predictor: Fallback, percentageError: 40m));

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<GetForecastAccuracySummaryQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetForecastAccuracySummaryQuery q, CancellationToken ct) =>
                new GetForecastAccuracySummaryQueryHandler(store).Handle(q, ct));

        var response = await new AdminForecastAccuracyController(mediator.Object).GetSummary(windowDays: 30);

        var ok = Assert.IsType<OkObjectResult>(response);
        var dto = Assert.IsType<ForecastAccuracySummary_GetDto>(ok.Value);
        dto.ByActivePredictor.Select(g => g.ActivePredictor).Should().BeEquivalentTo(new[] { Model, Fallback });

        // ?windowDays= binds and reaches the store as a cutoff date rather than being silently dropped.
        dto.WindowDays.Should().Be(30);
        store.CapturedFromSnapshotDate.Should().Be(DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30));
    }

    // The maturity-state strings on the wire are the shared .NET/Python contract, not free text: prove
    // the DTO carries them through unchanged and that the constants themselves are still lowercase.
    [Fact]
    public void MaturityStateConstants_AreTheLowercaseWireContract()
    {
        ForecastSnapshotMaturityStates.Pending.Should().Be("pending");
        ForecastSnapshotMaturityStates.Matured.Should().Be("matured");
        ForecastSnapshotMaturityStates.ActualUnavailable.Should().Be("actual_unavailable");
        ForecastSnapshotMaturityStates.NotMaturable.Should().Be("not_maturable");
    }

    // Guard against an accidental "overall"/"combined" field creeping onto the accuracy contract later.
    [Fact]
    public void Summary_Contract_HasNoOverallOrCombinedProperty()
    {
        var names = typeof(ForecastAccuracySummary_GetDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name.ToLowerInvariant())
            .ToList();

        names.Should().NotContain(n => n.Contains("overall"));
        names.Should().NotContain(n => n.Contains("combined"));
        names.Should().NotContain(n => n.Contains("blended"));
    }
}
