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
/// The store's EF LINQ is not covered here (same boundary as the Logs-hub tests); what IS covered is
/// everything that decides what number an admin ends up reading.
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

        public DateOnly? CapturedFromSnapshotDate;
        public Guid? CapturedCropId;
        public string? CapturedModelVersion;
        public bool? CapturedMaturedOnly;
        public int CapturedPage;
        public int CapturedPageSize;

        // Seeds a matured row inside the window (dated today), the common case for the metric tests.
        public void AddMatured(ForecastSnapshotScoringRow row, DateOnly? snapshotDate = null)
            => Matured.Add((row, snapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow)));

        public Task<ForecastSnapshotCensus> GetCensusAsync(CancellationToken ct = default)
            => Task.FromResult(Census);

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

        var m = MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model);

        m.MaturedCount.Should().Be(3);
        m.DirectionalScored.Should().Be(1);
        m.DirectionalExcluded.Should().Be(2);
        m.DirectionalAccuracy.Should().Be(1.0000m); // 1/1, NOT 1/3
    }

    // An exactly flat prediction is a hit only against an exactly flat actual (deadband 0, mirroring
    // the Python evaluate.directional_accuracy default).
    [Fact]
    public async Task Summary_DirectionalAccuracy_FlatPrediction_MatchesOnlyAFlatActual()
    {
        var store = new FakeStore();
        store.AddMatured(SRow(predictedPrice: 90m, actualPrice: 90m, referencePrice: 90m));  // hit
        store.AddMatured(SRow(predictedPrice: 90m, actualPrice: 95m, referencePrice: 90m));  // miss

        MetricsFor((await SummaryHandler(store).Handle(new GetForecastAccuracySummaryQuery(), default)).Data, Model)
            .DirectionalAccuracy.Should().Be(0.5000m);
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
