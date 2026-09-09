using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AgriForecast.API.Controllers;
using AgriForecast.Application.Dependency_Injection;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Common;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
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

    // Threshold 0 by default: the metric-VALUE tests below pin the unmasked arithmetic on deliberately
    // tiny populations, which the production minimum-sample gate would null wholesale. The gate has its
    // own tests at the production constant (the "minimum-sample gate" section), and production DI takes
    // the constant via the handler's parameter default.
    private static GetForecastAccuracySummaryQueryHandler SummaryHandler(
        FakeStore s, int minScoredCountForMetrics = 0) => new(s, minScoredCountForMetrics);
    private static GetForecastSnapshotsQueryHandler SnapshotsHandler(FakeStore s) => new(s);

    private const string Model = "residual";
    private const string Fallback = "crop_mean_fallback";

    // One crop unless a test says otherwise, so distinctCropCount is 1 and macro == micro by
    // construction in every test that is not about the crop axis.
    private static readonly Guid DefaultCropId = Guid.NewGuid();

    // One matured scoring row. The error columns are given explicitly, exactly as the maturing pass
    // freezes them, because that is what the handler must read rather than re-derive. The growth
    // period defaults to 90 — a MEDIUM-bucket value — so horizon-boundary tests must say what they
    // mean explicitly.
    private static ForecastSnapshotScoringRow SRow(
        string predictor = Model,
        string? modelVersion = "v17",
        decimal? percentageError = 10m,
        decimal? signedError = 5m,
        bool? withinInterval = true,
        decimal predictedPrice = 105m,
        decimal? actualPrice = 100m,
        decimal? referencePrice = 90m,
        Guid? cropId = null,
        string cropName = "Carrot",
        int? growthPeriodDays = 90)
        => new(predictor, modelVersion, predictedPrice, actualPrice, referencePrice,
            signedError, percentageError, withinInterval,
            cropId ?? DefaultCropId, cropName, growthPeriodDays);

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
        dto.ByHorizonBucket.Should().BeEmpty();
        // An empty worst-crops list still ships its threshold, so the page can say "no crop has 5
        // scored rows yet" instead of the misreading "no crop is bad".
        dto.WorstCrops.Should().BeEmpty();
        dto.WorstCropMinScoredCount.Should().Be(5);
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

    // THE HARD LAW (PRD §3.4). A model serving a crop well and a fallback serving it badly must never
    // average into one number: seed a 4% model and a 40% fallback ON THE SAME CROP and prove both
    // survive separately and that the 22% blend appears nowhere in the response. 5 rows each so the
    // crop qualifies for worstCrops under BOTH predictors — the exact fixture that once proved a
    // crop-keyed worstCrops entry publishing mape 22.00, the number this test forbids.
    [Fact]
    public async Task Summary_ModelAndFallback_AreSplit_AndNoBlendedNumberExists()
    {
        var store = new FakeStore();
        // Deterministic crop id: the serialized-response assertion below scans for "22", which a
        // random GUID could contain by coincidence.
        var crop = DeterministicGuid(1);
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(predictor: Model, cropId: crop, percentageError: 4m));
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(predictor: Fallback, cropId: crop, percentageError: 40m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByActivePredictor.Should().HaveCount(2);
        MetricsFor(dto, Model).Mape.Should().Be(4.00m);
        MetricsFor(dto, Model).MaturedCount.Should().Be(5);
        MetricsFor(dto, Fallback).Mape.Should().Be(40.00m);
        MetricsFor(dto, Fallback).MaturedCount.Should().Be(5);

        // worstCrops obeys the same law: the one crop appears once PER PREDICTOR, each entry carrying
        // that predictor's own figure — never one crop entry pooling the two into 22.00.
        dto.WorstCrops.Should().HaveCount(2);
        dto.WorstCrops.Single(c => c.ActivePredictor == Model).Mape.Should().Be(4.00m);
        dto.WorstCrops.Single(c => c.ActivePredictor == Fallback).Mape.Should().Be(40.00m);

        // The blend — MAPE 22.00 over all 10 rows — must not be reachable anywhere on the wire. The
        // FULL aggregate surface is serialized here (predictor groups, version groups, horizon buckets
        // AND worstCrops); generatedAtUtc is a timestamp and would match digits by coincidence, so it
        // stays out. The horizon buckets and worstCrops are in scope precisely because both are
        // predictor-keyed so nothing can pool the two predictors' rows back together.
        var everyMetric = dto.ByActivePredictor.Select(g => g.Metrics)
            .Concat(dto.ByModelVersion.Select(g => g.Metrics))
            .Concat(dto.ByHorizonBucket.Select(g => g.Metrics))
            .ToList();
        everyMetric.Should().NotContain(m => m.Mape == 22.00m);
        everyMetric.Should().NotContain(m => m.MedianApe == 22.00m);
        everyMetric.Should().NotContain(m => m.MaturedCount == 10); // no group covers all 10 rows
        dto.WorstCrops.Should().NotContain(c => c.Mape == 22.00m || c.MedianApe == 22.00m);

        JsonSerializer.Serialize(new
            { dto.ByActivePredictor, dto.ByModelVersion, dto.ByHorizonBucket, dto.WorstCrops })
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

    // ---------------------------------------------------------------- SUMMARY: horizon buckets

    private static ForecastAccuracyMetrics_GetDto BucketMetrics(
        ForecastAccuracySummary_GetDto dto, string predictor, string bucket) =>
        dto.ByHorizonBucket.Single(g => g.ActivePredictor == predictor && g.HorizonBucket == bucket)
            .Metrics;

    // THE BOUNDARY CONVENTION, pinned on the exact edge values: short = gp < 60, medium = 60..120
    // inclusive on BOTH ends, long = gp > 120. Each row carries a distinct APE so a row landing in the
    // wrong bucket shows up as a wrong mean, not just a wrong count.
    [Fact]
    public async Task Summary_HorizonBuckets_BoundaryRows_LandExactlyPerTheConvention()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(growthPeriodDays: 59, percentageError: 10m));   // short, by one day
        store.AddMatured(SRow(growthPeriodDays: 60, percentageError: 20m));   // medium — the boundary is medium
        store.AddMatured(SRow(growthPeriodDays: 120, percentageError: 30m));  // medium — upper edge included
        store.AddMatured(SRow(growthPeriodDays: 121, percentageError: 40m));  // long, by one day

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        BucketMetrics(dto, Model, "short").ScoredCount.Should().Be(1);
        BucketMetrics(dto, Model, "short").Mape.Should().Be(10.00m);
        BucketMetrics(dto, Model, "medium").ScoredCount.Should().Be(2);
        BucketMetrics(dto, Model, "medium").Mape.Should().Be(25.00m); // 60 and 120, nothing else
        BucketMetrics(dto, Model, "long").ScoredCount.Should().Be(1);
        BucketMetrics(dto, Model, "long").Mape.Should().Be(40.00m);

        // The bucket disclosures carry the observed spans, which is how an admin audits the bucketing.
        BucketMetrics(dto, Model, "medium").MinGrowthPeriodDays.Should().Be(60);
        BucketMetrics(dto, Model, "medium").MaxGrowthPeriodDays.Should().Be(120);
    }

    // The full key sequence: every predictor with matured rows gets ALL THREE named buckets in
    // short/medium/long order — empty ones included, because an EMPTY long bucket in a short window is
    // the survivorship signal itself — predictors ordered ordinally, and no unknown bucket when no row
    // needs one.
    [Fact]
    public async Task Summary_HorizonBuckets_EveryPredictorCarriesAllThreeNamedBuckets_EmptyOnesIncluded()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictor: Model, growthPeriodDays: 30, percentageError: 5m));
        store.AddMatured(SRow(predictor: Fallback, growthPeriodDays: 130, percentageError: 40m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.ByHorizonBucket.Select(g => (g.ActivePredictor, g.HorizonBucket)).Should().ContainInOrder(
            (Fallback, "short"), (Fallback, "medium"), (Fallback, "long"),
            (Model, "short"), (Model, "medium"), (Model, "long"));
        dto.ByHorizonBucket.Should().HaveCount(6, "no unknown bucket exists when no row lacks a growth period");

        // The empty buckets are the empty-Compute shape: zero counts, null metrics — never 0.0.
        var emptyLong = BucketMetrics(dto, Model, "long");
        emptyLong.MaturedCount.Should().Be(0);
        emptyLong.Mape.Should().BeNull();
        emptyLong.MinGrowthPeriodDays.Should().BeNull();
        emptyLong.DistinctCropCount.Should().Be(0);

        // And the occupied buckets never pooled across predictors: each one's row stayed its own.
        BucketMetrics(dto, Model, "short").Mape.Should().Be(5.00m);
        BucketMetrics(dto, Fallback, "long").Mape.Should().Be(40.00m);
        BucketMetrics(dto, Fallback, "short").MaturedCount.Should().Be(0);
    }

    // A matured row with no growth period should be impossible (the writer mints 'pending', the only
    // maturable state, only when a positive growth period resolved a harvest date) — but no DB
    // constraint enforces that, so such a row gets an explicit "unknown" bucket and is NEVER silently
    // dropped: the buckets must partition the group's matured rows exactly.
    [Fact]
    public async Task Summary_HorizonBuckets_NullGrowthPeriodRow_GetsTheUnknownBucket_NeverDropped()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(growthPeriodDays: 90, percentageError: 10m));
        store.AddMatured(SRow(growthPeriodDays: null, percentageError: 30m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        var unknown = BucketMetrics(dto, Model, "unknown");
        unknown.MaturedCount.Should().Be(1);
        unknown.Mape.Should().Be(30.00m);
        unknown.MinGrowthPeriodDays.Should().BeNull("there is no growth period to span");

        // The buckets partition the group: their matured counts sum to the predictor group's.
        dto.ByHorizonBucket.Where(g => g.ActivePredictor == Model).Sum(g => g.Metrics.MaturedCount)
            .Should().Be(MetricsFor(dto, Model).MaturedCount);
    }

    // ---------------------------------------------------------------- SUMMARY: macro vs micro

    // THE SIMPSON'S-PARADOX ALARM (audit Critical-3): one heavy crop with bad rows drags the MICRO
    // figure (every row equal) far above the MACRO figure (every crop equal). Micro answers "how wrong
    // is a typical prediction", macro "how wrong is a typical crop" — here the typical prediction is
    // bad (42.50) while the typical crop is much better (20.00), because 10 of 12 rows belong to the
    // one bad crop. The documented direction: heavy bad crop ⇒ micro ABOVE macro.
    [Fact]
    public async Task Summary_MacroAverages_DivergeFromMicro_WhenOneHeavyCropDominates()
    {
        var store = new FakeStore();
        var heavy = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
            store.AddMatured(SRow(cropId: heavy, cropName: "Beans", percentageError: 50m));
        store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Carrot", percentageError: 5m));
        store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Leeks", percentageError: 5m));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.DistinctCropCount.Should().Be(3);
        m.Mape.Should().Be(42.50m);          // (10·50 + 2·5) / 12 — the heavy crop dominates
        m.MacroMape.Should().Be(20.00m);     // (50 + 5 + 5) / 3 — each crop weighs the same
        m.MedianApe.Should().Be(50.00m);     // the typical ROW is a heavy-crop row
        m.MacroMedianApe.Should().Be(20.00m);
        m.Mape.Should().BeGreaterThan(m.MacroMape!.Value,
            "a few heavy crops dominating the pooled figure is exactly what the divergence reports");
    }

    // Over a single crop the two averages are THE SAME number by construction (both round once at
    // publication) — divergence is a crop-mix fact, never a rounding artifact. That duplication is
    // exactly why the WIRE never publishes a 1-crop macro: the identity is pinned on the pre-gate
    // Compute, and the handler path is pinned masking it even at threshold 0 — the crop bar is a
    // CONSTANT (MinDistinctCropsForMacro), not the row-threshold seam the value tests dial down.
    [Fact]
    public async Task Summary_MacroEqualsMicro_ForASingleCropGroup_AndTheWireMasksTheDuplicate()
    {
        var store = new FakeStore();
        foreach (var ape in new[] { 3m, 7m, 11m })
            store.AddMatured(SRow(percentageError: ape));

        var unmasked = ForecastAccuracyMath.Compute(store.Matured.Select(x => x.Row));
        unmasked.DistinctCropCount.Should().Be(1);
        unmasked.MacroMape.Should().Be(unmasked.Mape);
        unmasked.MacroMedianApe.Should().Be(unmasked.MedianApe);

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);
        m.Mape.Should().Be(7.00m); // the micro pair publishes at the tests' threshold 0
        m.MacroMape.Should().BeNull("1 crop is below MinDistinctCropsForMacro whatever the row threshold");
        m.MacroMedianApe.Should().BeNull();
    }

    // ---------------------------------------------------------------- SUMMARY: worst crops

    // Ranked by the (predictor, crop) pair's own medianApe, worst first; a pair below the per-crop
    // minimum is EXCLUDED however bad it looks — 4 rows at APE 99 is an anecdote, not the worst crop.
    [Fact]
    public async Task Summary_WorstCrops_RanksByMedianApeDescending_AndExcludesCropsBelowTheMinimum()
    {
        var store = new FakeStore();
        var beet = Guid.NewGuid();
        var carrot = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(cropId: beet, cropName: "Beetroot", percentageError: 40m));
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(cropId: carrot, cropName: "Carrot", percentageError: 10m));
        for (var i = 0; i < 4; i++) // one short of qualifying
            store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Leeks", percentageError: 99m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCropMinScoredCount.Should().Be(5);
        dto.WorstCrops.Should().HaveCount(2, "the 4-row crop does not qualify, whatever its APE");
        dto.WorstCrops.Select(c => c.CropName).Should().ContainInOrder("Beetroot", "Carrot");

        var worst = dto.WorstCrops[0];
        worst.ActivePredictor.Should().Be(Model); // every entry names the predictor it describes
        worst.CropId.Should().Be(beet);
        worst.ScoredCount.Should().Be(5);
        worst.CopyCount.Should().Be(0); // the default fixture rows are real predictions, not copies
        worst.MedianApe.Should().Be(40.00m);
        worst.Mape.Should().Be(40.00m);
    }

    // SPLIT LAW on the crop axis (PRD §3.4): a crop served by two predictors gets one entry PER
    // predictor, each carrying that predictor's own figures — never one entry pooling the rows into
    // the blended number the law forbids. And the qualification minimum counts each predictor's rows
    // alone: 3 model rows + 2 fallback rows on one crop qualify NOTHING (they used to pool to 5).
    [Fact]
    public async Task Summary_WorstCrops_AreKeyedByPredictorAndCrop_NeverPooled()
    {
        var store = new FakeStore();
        var crop = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(predictor: Model, cropId: crop, cropName: "Beans", percentageError: 20m));
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(predictor: Fallback, cropId: crop, cropName: "Beans", percentageError: 30m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().HaveCount(2, "one crop, two predictors — two entries, never a pooled one");
        var fallbackEntry = dto.WorstCrops.Single(c => c.ActivePredictor == Fallback);
        fallbackEntry.ScoredCount.Should().Be(5);
        fallbackEntry.MedianApe.Should().Be(30.00m);
        var modelEntry = dto.WorstCrops.Single(c => c.ActivePredictor == Model);
        modelEntry.ScoredCount.Should().Be(5);
        modelEntry.MedianApe.Should().Be(20.00m);
        // The worse-served entry ranks first, and neither entry is the 25.00 blend. (Assert.Same, not
        // .Should(): FluentAssertions 8 binds the enum overload on nullable-annotated references.)
        Assert.Same(fallbackEntry, dto.WorstCrops[0]);
        dto.WorstCrops.Should().NotContain(c => c.MedianApe == 25.00m || c.Mape == 25.00m);

        // Below-the-minimum rows of two predictors never combine to qualify a crop.
        var split = new FakeStore();
        var other = Guid.NewGuid();
        for (var i = 0; i < 3; i++)
            split.AddMatured(SRow(predictor: Model, cropId: other, cropName: "Leeks", percentageError: 20m));
        for (var i = 0; i < 2; i++)
            split.AddMatured(SRow(predictor: Fallback, cropId: other, cropName: "Leeks", percentageError: 30m));

        (await SummaryHandler(split).Handle(new GetForecastAccuracySummaryQuery(), default))
            .Data.WorstCrops.Should().BeEmpty("3 model + 2 fallback rows are two under-minimum groups, not 5");
    }

    // THE DEGENERATE-KEY FIX (review Blocker 2): copies (pred value-equal to the carry-forward anchor,
    // the same convention as predictionEqualsReferenceCount) have APE ≈ 0 by construction, so counting
    // them dragged a misled crop's median to zero and ranked it BELOW a steady crop. The ranking
    // figures must cover NON-COPY rows only, with the discount disclosed via scoredCount/copyCount.
    [Fact]
    public async Task Summary_WorstCrops_CopiesNeverDragTheRanking_AndTheDiscountIsDisclosed()
    {
        var store = new FakeStore();
        var misled = Guid.NewGuid();
        var steady = Guid.NewGuid();
        // The misled crop: 10 fallback-style copies at APE 0 burying 5 real misses at APE 90.
        for (var i = 0; i < 10; i++)
            store.AddMatured(SRow(cropId: misled, cropName: "Beans", predictedPrice: 100m,
                referencePrice: 100m, actualPrice: 100m, percentageError: 0m, signedError: 0m));
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(cropId: misled, cropName: "Beans", percentageError: 90m));
        // The steady crop: 5 real predictions at APE 6.
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(cropId: steady, cropName: "Carrot", percentageError: 6m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().HaveCount(2);
        var worst = dto.WorstCrops[0];
        worst.CropName.Should().Be("Beans", "the crop farmers are misled about ranks FIRST — " +
            "under the old copy-counting median [0×10, 90×5] it ranked below the steady crop");
        worst.MedianApe.Should().Be(90.00m); // over the 5 measured forecasts only
        worst.Mape.Should().Be(90.00m);
        worst.ScoredCount.Should().Be(15);   // the population disclosure
        worst.CopyCount.Should().Be(10);     // ...and how much of it was discounted
        dto.WorstCrops[1].CropName.Should().Be("Carrot");
        dto.WorstCrops[1].CopyCount.Should().Be(0);
    }

    // The qualification minimum counts NON-COPY rows: 10 copies cannot promote 4 measured misses into
    // a ranked entry — 4 measured forecasts is an anecdote whatever the copy traffic around it.
    [Fact]
    public async Task Summary_WorstCrops_QualificationCountsNonCopyRowsOnly()
    {
        var store = new FakeStore();
        var crop = Guid.NewGuid();
        for (var i = 0; i < 10; i++)
            store.AddMatured(SRow(cropId: crop, cropName: "Beans", predictedPrice: 100m,
                referencePrice: 100m, actualPrice: 100m, percentageError: 0m, signedError: 0m));
        for (var i = 0; i < 4; i++) // one measured row short
            store.AddMatured(SRow(cropId: crop, cropName: "Beans", percentageError: 90m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().BeEmpty("scoredCount is 14 but only 4 rows are measured forecasts");
        dto.WorstCropMinScoredCount.Should().Be(5);
    }

    // TODAY'S LIVE SHAPE: every matured row is a fallback copy. A crop with no measured forecasts
    // cannot be ranked — there is no median of zero real predictions to rank it by — so the honest
    // list is EMPTY with the threshold exposed, not a list of medianApe-0.00 entries whose ranking
    // fell entirely to the tie-break.
    [Fact]
    public async Task Summary_WorstCrops_AllCopyCrop_CannotBeRanked_TheEmptyListIsTheHonestAnswer()
    {
        var store = new FakeStore();
        for (var i = 0; i < 16; i++)
            store.AddMatured(SRow(predictor: Fallback, cropName: "Beans", predictedPrice: 90m,
                referencePrice: 90m, actualPrice: 90m, percentageError: 0m, signedError: 0m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().BeEmpty("16 scored rows, 0 measured forecasts");
        dto.WorstCropMinScoredCount.Should().Be(5);
    }

    // Two honesty bars, both disclosed (review should-fix B): entries qualify at 5 non-copy rows (the
    // triage threshold) but are BADGED against the same 30-row bar the group metrics publish under —
    // flagged, not hidden, because hiding a small-n entry would un-rank the very crops the list exists
    // to surface. The badge covers the non-copy population, not scoredCount.
    [Fact]
    public async Task Summary_WorstCrops_PerEntryMeetsMinimumSample_UsesTheMetricsGateOverNonCopyRows()
    {
        var store = new FakeStore();
        var small = Guid.NewGuid();
        var large = Guid.NewGuid();
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(cropId: small, cropName: "Beans", percentageError: 80m));
        for (var i = 0; i < 30; i++)
            store.AddMatured(SRow(cropId: large, cropName: "Carrot", percentageError: 50m));
        // 10 copies on the large crop: scoredCount 40, but the badge must key on the 30 measured rows.
        for (var i = 0; i < 10; i++)
            store.AddMatured(SRow(cropId: large, cropName: "Carrot", predictedPrice: 100m,
                referencePrice: 100m, actualPrice: 100m, percentageError: 0m, signedError: 0m));

        // The PRODUCTION handler: the badge threshold rides the same ctor seam as the group gate.
        var dto = (await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().HaveCount(2);
        var smallEntry = dto.WorstCrops[0]; // worst-ranked AND small-sample — ranked, badged, not hidden
        smallEntry.CropName.Should().Be("Beans");
        smallEntry.MedianApe.Should().Be(80.00m);
        smallEntry.MeetsMinimumSample.Should().BeFalse();
        var largeEntry = dto.WorstCrops[1];
        largeEntry.ScoredCount.Should().Be(40);
        largeEntry.CopyCount.Should().Be(10);
        largeEntry.MeetsMinimumSample.Should().BeTrue("30 measured rows meet the metrics gate");
    }

    // A triage list, not a report: capped at 10, keeping the 10 WORST.
    [Fact]
    public async Task Summary_WorstCrops_CapAtTen_KeepsTheWorstTen()
    {
        var store = new FakeStore();
        for (var crop = 1; crop <= 11; crop++)
            for (var i = 0; i < 5; i++)
                store.AddMatured(SRow(cropId: DeterministicGuid(crop),
                    cropName: $"Crop{crop:00}", percentageError: crop * 2m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().HaveCount(10);
        dto.WorstCrops[0].CropName.Should().Be("Crop11"); // APE 22, the worst
        dto.WorstCrops.Select(c => c.CropName).Should().NotContain("Crop01",
            "the LEAST bad crop is the one the cap drops");
        dto.WorstCrops.Select(c => c.MedianApe).Should().BeInDescendingOrder();
    }

    // THE CAP IS PER PREDICTOR (re-review S-2): a single global Take(10) let 11 bad fallback crops
    // fill every slot and the model's worst crops never appeared — the (predictor, crop) re-keying
    // had re-introduced starvation through the cap. Each predictor now contributes up to 10 of its
    // OWN worst entries; the concatenation is re-sorted by the one display ordering, so the result
    // is still a single deterministic worst-first list.
    [Fact]
    public async Task Summary_WorstCrops_CapAppliesPerPredictor_SoOnePredictorCannotStarveTheOther()
    {
        var store = new FakeStore();
        for (var crop = 1; crop <= 11; crop++) // fallback: 11 qualifying crops at APE 5..55
            for (var i = 0; i < 5; i++)
                store.AddMatured(SRow(predictor: Fallback, modelVersion: null,
                    cropId: DeterministicGuid(crop), cropName: $"Fallback{crop:00}",
                    percentageError: crop * 5m));
        for (var i = 0; i < 5; i++) // model: one crop worse than every fallback crop...
            store.AddMatured(SRow(cropId: DeterministicGuid(100), cropName: "ModelWorst",
                percentageError: 60m));
        for (var i = 0; i < 5; i++) // ...and one milder than all of them
            store.AddMatured(SRow(cropId: DeterministicGuid(101), cropName: "ModelMild",
                percentageError: 3m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        // 10 + 2, not a global 10: the fallback is truncated to ITS 10, the model keeps both entries.
        dto.WorstCrops.Should().HaveCount(12);
        dto.WorstCrops.Count(c => c.ActivePredictor == Fallback).Should().Be(10);
        dto.WorstCrops.Count(c => c.ActivePredictor == Model).Should().Be(2);
        dto.WorstCrops.Select(c => c.CropName).Should().NotContain("Fallback01",
            "the crop the cap drops is the FALLBACK'S least bad, not the list's globally least bad");
        dto.WorstCrops[0].CropName.Should().Be("ModelWorst"); // APE 60 tops the re-sorted display list
        dto.WorstCrops[^1].CropName.Should().Be("ModelMild",
            "APE 3 is milder than the dropped fallback crop's 5, but the cap is per predictor — " +
            "it stays, re-sorted to the bottom for display");
        dto.WorstCrops.Select(c => c.MedianApe).Should().BeInDescendingOrder(
            "the concatenation is re-sorted by the display ordering, deterministically");
    }

    // Data present but nothing qualifying is still an EMPTY list — with the threshold on the wire, so
    // the page can say why instead of implying every crop is fine.
    [Fact]
    public async Task Summary_WorstCrops_EmptyWhenNoCropQualifies_WithTheThresholdStillOnTheWire()
    {
        var store = new FakeStore();
        for (var i = 0; i < 4; i++)
            store.AddMatured(SRow(percentageError: 80m));

        var dto = (await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        dto.WorstCrops.Should().BeEmpty();
        dto.WorstCropMinScoredCount.Should().Be(5);
    }

    private static Guid DeterministicGuid(int n) =>
        new(n, 0, 0, new byte[8]); // distinct, stable, ordinal-comparable — enough for ranking fixtures

    // ---------------------------------------------------------------- SUMMARY: the minimum-sample gate

    // Handler at the PRODUCTION threshold (the constructor default DI uses), which the value tests
    // above deliberately bypass with threshold 0.
    private static GetForecastAccuracySummaryQueryHandler ProductionGateHandler(FakeStore s) => new(s);

    // One row short of the gate: every magnitude/rate metric is null, every count and disclosure
    // survives, and the wire says which regime it is in and what the bar is. The page renders
    // "n=29, below the 30-row minimum" — not a number computed over 29 rows dressed up as a verdict.
    [Fact]
    public async Task Summary_Gate_At29ScoredRows_MasksTheMetrics_ButKeepsEveryCountAndDisclosure()
    {
        var store = new FakeStore();
        for (var i = 0; i < 29; i++)
            store.AddMatured(SRow(percentageError: 5m, signedError: 5m,
                predictedPrice: 105m, referencePrice: 90m, actualPrice: 100m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MeetsMinimumSample.Should().BeFalse();
        m.MinScoredCountForMetrics.Should().Be(30);
        ForecastAccuracyMath.MinScoredCountForMetrics.Should().Be(30);

        // Masked: everything a reader would take as an accuracy verdict.
        m.Mape.Should().BeNull();
        m.MedianApe.Should().BeNull();
        m.SignedBias.Should().BeNull();
        m.BaselineMape.Should().BeNull();
        m.BaselineMedianApe.Should().BeNull();
        m.SkillVsBaseline.Should().BeNull();
        m.IntervalCoverage.Should().BeNull();
        m.IntervalCoverageGap.Should().BeNull();
        m.DirectionalAccuracy.Should().BeNull();
        m.MacroMape.Should().BeNull();
        m.MacroMedianApe.Should().BeNull();

        // Kept: the whole population picture.
        m.MaturedCount.Should().Be(29);
        m.ScoredCount.Should().Be(29);
        m.BaselineScoredCount.Should().Be(29);
        m.IntervalScoredCount.Should().Be(29);
        m.WithinIntervalCount.Should().Be(29);
        m.DirectionalScored.Should().Be(29);
        m.DistinctCropCount.Should().Be(1);
        m.MinGrowthPeriodDays.Should().Be(90);
        m.MaxGrowthPeriodDays.Should().Be(90);
        m.NominalIntervalCoverage.Should().Be(0.80m); // the yardstick is a constant, not a measurement
    }

    // The 30th row opens the gate: same seed plus one, and every metric publishes — except the macro
    // pair, whose SECOND bar (MinDistinctCropsForMacro crops) a single-crop group can never clear;
    // the crop-bar tests below cover both sides of that bar.
    [Fact]
    public async Task Summary_Gate_At30ScoredRows_PublishesTheMetrics()
    {
        var store = new FakeStore();
        for (var i = 0; i < 30; i++)
            store.AddMatured(SRow(percentageError: 5m, signedError: 5m,
                predictedPrice: 105m, referencePrice: 90m, actualPrice: 100m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MeetsMinimumSample.Should().BeTrue();
        m.Mape.Should().Be(5.00m);
        m.MedianApe.Should().Be(5.00m);
        m.SignedBias.Should().Be(5.00m);
        m.SkillVsBaseline.Should().Be(0.50m); // 5.00 / 10.00
        m.IntervalCoverage.Should().Be(1.0000m);
        m.DirectionalAccuracy.Should().Be(1.0000m);
        m.MacroMape.Should().BeNull("30 rows clear the row bar, but 1 crop is below the macro pair's " +
            "crop bar — a 1-crop macro would merely duplicate the micro figure above");
    }

    // THE MACRO CROP BAR, at the re-review's exact failure case: 29 rows of one crop at APE 50 plus
    // 1 row of another at APE 0. The row gate opens (scored 30) and the micro pair publishes — but a
    // 2-crop macro would print 25.00 with a SINGLE ROW carrying half the crop-weight, so the macro
    // pair stays null below MinDistinctCropsForMacro crops, with distinctCropCount on the wire to
    // say why.
    [Fact]
    public async Task Summary_Gate_MacroPair_StaysNullBelowThreeCrops_WhileTheMicroPairPublishes()
    {
        var store = new FakeStore();
        var heavy = Guid.NewGuid();
        for (var i = 0; i < 29; i++)
            store.AddMatured(SRow(cropId: heavy, cropName: "Beans", percentageError: 50m));
        store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Carrot", percentageError: 0m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        ForecastAccuracyMath.MinDistinctCropsForMacro.Should().Be(3);
        m.ScoredCount.Should().Be(30);
        m.MeetsMinimumSample.Should().BeTrue(); // the headline decision keys on rows alone
        m.Mape.Should().Be(48.33m);             // (29·50 + 0) / 30 — the micro pair publishes
        m.MedianApe.Should().Be(50.00m);
        m.DistinctCropCount.Should().Be(2);     // ...but 2 crops cannot say what a "typical crop" does
        m.MacroMape.Should().BeNull();
        m.MacroMedianApe.Should().BeNull();
    }

    // The third crop opens the crop bar: the same heavy-crop shape with one more crop, and the macro
    // pair publishes with equal CROP weight.
    [Fact]
    public async Task Summary_Gate_MacroPair_PublishesAtThreeCrops()
    {
        var store = new FakeStore();
        var heavy = Guid.NewGuid();
        for (var i = 0; i < 28; i++)
            store.AddMatured(SRow(cropId: heavy, cropName: "Beans", percentageError: 50m));
        store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Carrot", percentageError: 0m));
        store.AddMatured(SRow(cropId: Guid.NewGuid(), cropName: "Leeks", percentageError: 10m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(30);
        m.DistinctCropCount.Should().Be(3);
        m.MacroMape.Should().Be(20.00m);      // (50 + 0 + 10) / 3 — each crop weighs the same
        m.MacroMedianApe.Should().Be(20.00m); // per-crop medians 50 / 0 / 10, same equal weight
    }

    // TODAY'S LIVE HEADLINE, gated on purpose (audit Critical-3): 16 fallback copy rows used to print
    // MAPE 4.92 as if it meant something. Under the gate the verdict metrics are null — but the A1
    // copy counts, the A2 degenerate bucket and the A3 census all survive untouched, so the page still
    // shows WHAT the 16 rows are (all copies, none directionally scorable, ledger alive), just not a
    // 16-row number wearing a verdict's clothes.
    [Fact]
    public async Task Summary_Gate_LeavesTheCopyCounts_TheDegenerateBucket_AndTheCensusUntouched()
    {
        var store = new FakeStore();
        for (var i = 0; i < 16; i++)
            store.AddMatured(SRow(predictor: Fallback, modelVersion: null, predictedPrice: 90m,
                actualPrice: 90m, referencePrice: 90m, percentageError: 0m, signedError: 0m));
        store.AddPending(Fallback, null, harvestDate: DateOnly.FromDateTime(DateTime.UtcNow).AddDays(20));

        var dto = (await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;
        var g = dto.ByActivePredictor.Single(x => x.ActivePredictor == Fallback);

        g.Metrics.MeetsMinimumSample.Should().BeFalse();
        g.Metrics.Mape.Should().BeNull();
        g.Metrics.DirectionalAccuracy.Should().BeNull();

        // A1: the copy disclosure survives — a fact about what the predictions ARE, not a skill claim.
        g.Metrics.PredictionEqualsReferenceCount.Should().Be(16);
        g.Metrics.PredictionEqualsReferenceShare.Should().Be(1.0000m);
        // A2: the degenerate partition survives.
        g.Metrics.DirectionalScored.Should().Be(0);
        g.Metrics.DirectionalDegenerate.Should().Be(16);
        g.Metrics.DirectionalExcluded.Should().Be(0);
        // A3: the census survives whole.
        g.Census.Matured.Should().Be(16);
        g.Census.Pending.Should().Be(1);
        g.Census.Total.Should().Be(17);
    }

    // The gate is per GROUP, not per response: a model bucket with 30 scored rows publishes while the
    // fallback's 5-row groups mask, in the same summary — on the predictor list, the version list and
    // the horizon buckets alike.
    [Fact]
    public async Task Summary_Gate_AppliesPerGroup_AcrossPredictorVersionAndBucketLists()
    {
        var store = new FakeStore();
        for (var i = 0; i < 30; i++)
            store.AddMatured(SRow(predictor: Model, modelVersion: "v17",
                growthPeriodDays: 30, percentageError: 5m));
        for (var i = 0; i < 5; i++)
            store.AddMatured(SRow(predictor: Fallback, modelVersion: null, percentageError: 40m));

        var dto = (await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data;

        MetricsFor(dto, Model).Mape.Should().Be(5.00m);
        MetricsFor(dto, Fallback).Mape.Should().BeNull();
        MetricsFor(dto, Fallback).ScoredCount.Should().Be(5);

        dto.ByModelVersion.Single(g => g.ModelVersion == "v17").Metrics.Mape.Should().Be(5.00m);
        dto.ByModelVersion.Single(g => g.ModelVersion == null).Metrics.Mape.Should().BeNull();

        BucketMetrics(dto, Model, "short").Mape.Should().Be(5.00m);
        BucketMetrics(dto, Model, "short").MeetsMinimumSample.Should().BeTrue();
        BucketMetrics(dto, Fallback, "medium").Mape.Should().BeNull();
        BucketMetrics(dto, Fallback, "medium").ScoredCount.Should().Be(5);
    }

    // PER-DENOMINATOR GATING (review should-fix A). The page prints each rate beside its OWN n, so
    // the gate must key on that same n. Probe (i): 30 scored rows of which 28 are copies — the
    // directional figure covers directionalScored=2 rows and would have published 100% under a
    // scoredCount-keyed gate. It must be null while the headline mape (n=30) publishes.
    [Fact]
    public async Task Summary_Gate_DirectionalAccuracy_IsGatedOnDirectionalScored_NotScoredCount()
    {
        var store = new FakeStore();
        for (var i = 0; i < 28; i++) // copies: scored, but directionally DEGENERATE
            store.AddMatured(SRow(predictedPrice: 90m, referencePrice: 90m, actualPrice: 100m,
                percentageError: -10m, signedError: -10m));
        for (var i = 0; i < 2; i++)  // the only two real directional claims — both hits
            store.AddMatured(SRow(predictedPrice: 105m, referencePrice: 90m, actualPrice: 100m,
                percentageError: 5m, signedError: 5m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(30);
        m.MeetsMinimumSample.Should().BeTrue(); // the headline population meets the bar...
        m.Mape.Should().NotBeNull();
        m.DirectionalScored.Should().Be(2);
        m.DirectionalDegenerate.Should().Be(28);
        m.DirectionalAccuracy.Should().BeNull("2 directionally scored rows are below the 30-row bar, " +
            "whatever scoredCount says — the 100% would have been printed beside n=2");
    }

    // Probe (ii): 30 scored rows of which only 3 carry the plant-day anchor — the baseline trio covers
    // baselineScoredCount=3 rows and must be null while the headline (n=30) publishes.
    [Fact]
    public async Task Summary_Gate_BaselineAndSkill_AreGatedOnBaselineScoredCount_NotScoredCount()
    {
        var store = new FakeStore();
        for (var i = 0; i < 27; i++) // anchorless: scored, but invisible to the baseline
            store.AddMatured(SRow(referencePrice: null, percentageError: 5m, signedError: 5m));
        for (var i = 0; i < 3; i++)
            store.AddMatured(SRow(predictedPrice: 105m, referencePrice: 90m, actualPrice: 100m,
                percentageError: 5m, signedError: 5m));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(30);
        m.MeetsMinimumSample.Should().BeTrue();
        m.Mape.Should().Be(5.00m);
        m.BaselineScoredCount.Should().Be(3);
        m.BaselineMape.Should().BeNull("3 anchored rows are below the 30-row bar");
        m.BaselineMedianApe.Should().BeNull();
        m.SkillVsBaseline.Should().BeNull();
    }

    // Probe (iii), the other direction: 210 interval verdicts on rows whose error columns are null —
    // intervalCoverage covers intervalScoredCount=210 and must PUBLISH, even though scoredCount=10
    // masks the error metrics. A scoredCount-keyed gate showed "n=210" beside a null labelled
    // below-minimum.
    [Fact]
    public async Task Summary_Gate_IntervalCoverage_IsGatedOnIntervalScoredCount_AndPublishesWhileMapeMasks()
    {
        var store = new FakeStore();
        for (var i = 0; i < 200; i++) // interval verdict present, error columns never written
            store.AddMatured(SRow(percentageError: null, signedError: null, withinInterval: true));
        for (var i = 0; i < 10; i++)
            store.AddMatured(SRow(percentageError: 5m, signedError: 5m, withinInterval: false));

        var m = MetricsFor((await ProductionGateHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(10);
        m.MeetsMinimumSample.Should().BeFalse(); // ...and the headline flag says so
        m.Mape.Should().BeNull();
        m.IntervalScoredCount.Should().Be(210);
        m.IntervalCoverage.Should().Be(0.9524m, "210 interval verdicts are ABOVE the bar — masking " +
            "them because the error metrics are thin would print n=210 beside an unexplained null");
        m.IntervalCoverageGap.Should().Be(0.1524m);
    }

    // ---------------------------------------------------------------- SUMMARY: growth-period disclosures

    // The span covers the SCORED rows — the population the metrics describe. A matured-but-unscored
    // row's growth period must not stretch the range the page prints beside the metrics, and a group
    // with nothing scored answers null, never a fabricated 0.
    [Fact]
    public async Task Summary_GrowthPeriodSpan_CoversScoredRowsOnly_AndIsNullWhenNothingIsScored()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(growthPeriodDays: 30, percentageError: 5m));
        store.AddMatured(SRow(growthPeriodDays: 150, percentageError: 8m));
        // Matured but never scored (null error columns): its 200 days is not part of the metrics'
        // population.
        store.AddMatured(SRow(growthPeriodDays: 200, percentageError: null, signedError: null));

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.ScoredCount.Should().Be(2);
        m.MinGrowthPeriodDays.Should().Be(30);
        m.MaxGrowthPeriodDays.Should().Be(150); // NOT 200 — the unscored row is not in the population
        m.DistinctCropCount.Should().Be(1);

        // The survivorship reading this disclosure exists for: a fallback group whose scored rows top
        // out at 45 days is a group long-horizon crops CANNOT have matured into yet.
        var shortOnly = new FakeStore();
        shortOnly.AddMatured(SRow(predictor: Fallback, growthPeriodDays: 45, percentageError: 5m));
        var f = MetricsFor((await SummaryHandler(shortOnly).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Fallback);
        f.MaxGrowthPeriodDays.Should().Be(45);

        // Nothing scored at all ⇒ null span, zero crops.
        var unscored = new FakeStore();
        unscored.AddMatured(SRow(percentageError: null, signedError: null));
        var u = MetricsFor((await SummaryHandler(unscored).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);
        u.MinGrowthPeriodDays.Should().BeNull();
        u.MaxGrowthPeriodDays.Should().BeNull();
        u.DistinctCropCount.Should().Be(0);
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

    // ---------------------------------------------------------------- DI SEAM

    // THE PRODUCTION THRESHOLD, resolved through real DI (review should-fix D). The handler's gate
    // threshold is a DEFAULTED ctor int — production takes the default because MS.DI cannot resolve an
    // int. That seam is silently overridable: anyone registering an `int` service (or adding a second
    // constructor) changes the production threshold with every handler unit test still green, because
    // those construct the handler by hand. So build the container the way the API's composition root
    // does — Program.cs calls AddApplicationLayer() for MediatR/validators; the store registration
    // mirrors InfsDependencyInjection's AddScoped line (AddInfrastructure itself needs a configured
    // DbContext, which this handler never touches) — resolve through IMediator, and pin the threshold
    // the resolved handler actually ran with.
    [Fact]
    public async Task Summary_ResolvedThroughRealDi_RunsAtTheProductionThreshold()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++) // enough to publish under the tests' threshold 0, not under 30
            store.AddMatured(SRow(percentageError: 5m));

        var services = new ServiceCollection();
        // The real API host registers logging by default; without it MediatR 13's LicenseAccessor
        // cannot be constructed from a bare ServiceCollection.
        services.AddLogging();
        services.AddApplicationLayer();
        services.AddScoped<IForecastAccuracyReadStore>(_ => store);

        // The named gap, closed as far as the composed collection goes: the threshold seam exists
        // because MS.DI cannot resolve an int — so a rogue ServiceDescriptor with ServiceType
        // typeof(int) is exactly what would satisfy the defaulted ctor parameter and silently move
        // the production threshold. Assert the composition contains none. (AddInfrastructure is
        // deliberately not composed here — see the header — so the guard covers the collection this
        // test actually builds, not the full API host.)
        services.Should().NotContain(d => d.ServiceType == typeof(int),
            "an `int` registration would override the handler's defaulted gate-threshold seam");

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetForecastAccuracySummaryQuery());

        result.IsSuccess.Should().BeTrue();
        var m = MetricsFor(result.Data, Model);
        m.MinScoredCountForMetrics.Should().Be(30,
            "the DI-resolved handler must run at ForecastAccuracyMath.MinScoredCountForMetrics — " +
            "an `int` registration or a second constructor would silently change this");
        m.ScoredCount.Should().Be(5);
        m.MeetsMinimumSample.Should().BeFalse();
        m.Mape.Should().BeNull("5 rows must be gated at the production threshold");
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
