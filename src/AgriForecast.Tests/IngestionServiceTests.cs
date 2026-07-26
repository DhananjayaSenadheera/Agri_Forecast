using System.Net;
using System.Text;
using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Services.CbslIngestion;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;
using AgriForecast.Infrastructure.Services.HartiIngestion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Behavioural tests for the R1.1 P1 Step 6 ingestion services: HartiBulletinIngestionService
/// (HTTP->Python seam + watermark advance/hold) and CbslPriceReportIngestionService (disabled
/// no-op that is NOT a source failure; loud enabled path). The HttpClient seam is mocked with a
/// scripted HttpMessageHandler; the watermark store with an in-memory fake so transitions can be
/// asserted directly.
/// </summary>
public class IngestionServiceTests
{
    // Test doubles.

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }
        // Snapshot the request body BEFORE returning: the HttpClient disposes the request
        // content once SendAsync completes, so reading it in the test afterwards would fail.
        public string? LastRequestBody { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }

    private sealed class FakeWatermarkRepository : IIngestionWatermarkRepository
    {
        private readonly Dictionary<string, IngestionWatermark> _store = new();
        public int SaveCount { get; private set; }

        public Task<IngestionWatermark?> GetAsync(string source, CancellationToken ct = default)
            => Task.FromResult(_store.TryGetValue(source, out var wm) ? wm : null);

        public Task<IngestionWatermark> GetOrCreateAsync(
            string source,
            IngestionSourceStatus initialStatus = IngestionSourceStatus.Ok,
            string? initialMessage = null,
            CancellationToken ct = default)
        {
            if (!_store.TryGetValue(source, out var wm))
            {
                wm = IngestionWatermark.Create(source, initialStatus, initialMessage);
                _store[source] = wm;
            }
            return Task.FromResult(wm);
        }

        public Task SaveChangesAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public IngestionWatermark? Peek(string source) => _store.GetValueOrDefault(source);

        public void Seed(IngestionWatermark wm) => _store[wm.Source] = wm;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpClient ClientFrom(StubHttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://ml.test/") };

    // HARTI: the happy path advances the watermark.

    [Fact]
    public async Task Harti_Success_AdvancesWatermark_AndSendsApiKeyAndSinceDate()
    {
        var wms = new FakeWatermarkRepository();
        // Seed a prior success so we can assert sinceDate is sent and the watermark advances.
        var seeded = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 6, 30, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 6, 29));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
            {"status":"ok",
             "priceObservations":{"inserted":12,"updated":3,"skippedNoMarket":1,"maxObservedDate":"2026-07-01"},
             "heal":{"healed":2,"unresolved":4},
             "outliers":{"nFlagged":1},
             "gaps":{"nInfo":5,"nWarning":2,"nError":0}}
            """));

        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        // Watermark advanced to the newest observed date returned by Python.
        var wm = wms.Peek(HartiBulletinIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Ok);
        wm.LastObservedDate.Should().Be(new DateOnly(2026, 7, 1));
        wm.LastSuccessUtc.Should().NotBeNull();

        // Auth header sent; resume sinceDate carried the prior high-water mark.
        handler.LastRequest!.Headers.Contains("X-API-Key").Should().BeTrue();
        handler.LastRequest.Headers.GetValues("X-API-Key").Should().ContainSingle().Which.Should().Be("secret-key");
        // Late-arrival look-back: sinceDate = LastObservedDate(2026-06-29) - default 7 days = 2026-06-22.
        // This pins the subtraction, NOT the raw watermark — a strict > resume would skip a late bulletin.
        handler.LastRequestBody.Should().Contain("2026-06-22",
            "sinceDate must be the watermark minus the default 7-day late-arrival look-back");
        handler.LastRequestBody.Should().NotContain("2026-06-29",
            "the raw watermark must not be sent as sinceDate — the look-back must be applied");
    }

    // HARTI: IngestAsync returns run-tracking stats mapped from the Python response. The Worker attaches
    // these to the run row: inserted -> RowsInserted, skippedNoMarket -> RowsSkipped, and the coverage
    // window runs from the requested resume lower bound to the newest landed ObservedDate.
    [Fact]
    public async Task Harti_Success_ReturnsStats_MappedFromResponse()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 6, 30, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 6, 29));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
            {"status":"ok",
             "priceObservations":{"inserted":12,"updated":3,"skippedNoMarket":1,"maxObservedDate":"2026-07-01"}}
            """));

        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.RowsInserted.Should().Be(12);
        stats.RowsSkipped.Should().Be(1, "skippedNoMarket maps to RowsSkipped");
        stats.CoveredToDate.Should().Be(new DateOnly(2026, 7, 1), "maxObservedDate is the coverage upper bound");
        // Coverage from-date = the requested resume lower bound = watermark(2026-06-29) - default 7d.
        stats.CoveredFromDate.Should().Be(new DateOnly(2026, 6, 22));
    }

    // HARTI: a disabled early return reports Outcome=Skipped, never a green success.
    [Fact]
    public async Task Harti_Disabled_ReportsSkippedOutcome()
    {
        var wms = new FakeWatermarkRepository();
        var disabled = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        disabled.Disable("manually paused");
        wms.Seed(disabled);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Skipped, "a disabled source is a skip, not a success");
        stats.RowsInserted.Should().BeNull();
    }

    // HARTI: a non-200 fail-safe early return reports Outcome=Failed plus a reason.
    [Fact]
    public async Task Harti_HttpError_ReportsFailedOutcome_WithReason_WithoutThrowing()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 6, 30, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 6, 29));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadGateway, "boom"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed, "a real transport failure must not render as a green Succeeded run row");
        stats.FailureReason.Should().Contain("502", "the run-row reason mirrors the watermark reason");
    }

    // HARTI: the look-back window is configurable.

    [Fact]
    public async Task Harti_SinceDate_HonoursConfiguredLookbackDays()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 6, 30, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 6, 29));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"status":"ok","priceObservations":{"inserted":0,"updated":0,"skippedNoMarket":0,"maxObservedDate":"2026-06-29"}}"""));

        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key"), ("MlService:HartiLookbackDays", "3")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        // 2026-06-29 - 3 days = 2026-06-26.
        handler.LastRequestBody.Should().Contain("2026-06-26",
            "a configured look-back of 3 days must be subtracted from the watermark");
    }

    // HARTI: a null watermark means a full backfill (sinceDate omitted).

    [Fact]
    public async Task Harti_NullWatermark_SendsNullSinceDate_ForFullBackfill()
    {
        var wms = new FakeWatermarkRepository();
        // No prior success: LastObservedDate is null. The look-back must NOT invent a date.
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"status":"ok","priceObservations":{"inserted":100,"updated":0,"skippedNoMarket":0,"maxObservedDate":"2020-01-01"}}"""));

        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        // A null watermark stays null => full backfill; the body carries a null sinceDate.
        handler.LastRequestBody.Should().Contain("null",
            "a null watermark must serialise sinceDate as null (full backfill), never a fabricated date");
    }

    // HARTI: a missing admin key fails loud.

    [Fact]
    public async Task Harti_Throws_WhenAdminKeyMissing()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0, "no HTTP call must be made without an admin key");
    }

    // HARTI: a transport or HTTP failure holds the resume point and never throws to the Worker.

    [Fact]
    public async Task Harti_HttpError_RecordsFailure_ButDoesNotThrow_NorAdvanceResumePoint()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 6, 30, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 6, 29));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadGateway, "boom"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        // Must NOT throw — the service fails safe; the Worker's outer try/catch is a second belt.
        await svc.IngestAsync(CancellationToken.None);

        var wm = wms.Peek(HartiBulletinIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Failed);
        wm.LastObservedDate.Should().Be(new DateOnly(2026, 6, 29),
            "a failed pass must not advance the resume point");
    }

    [Fact]
    public async Task Harti_TransportException_RecordsFailure_ButDoesNotThrow()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        wms.Peek(HartiBulletinIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Failed);
    }

    // HARTI: a Disabled watermark is a no-op — no HTTP call, and not a failure.

    [Fact]
    public async Task Harti_DisabledWatermark_IsNoOp_AndMakesNoHttpCall()
    {
        var wms = new FakeWatermarkRepository();
        var disabled = IngestionWatermark.Create(HartiBulletinIngestionService.SourceKey);
        disabled.Disable("manually paused");
        wms.Seed(disabled);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var svc = new HartiBulletinIngestionService(
            ClientFrom(handler),
            Config(("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<HartiBulletinIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        handler.Calls.Should().Be(0);
        wms.Peek(HartiBulletinIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Disabled,
            "a disabled source stays disabled — it is never mistaken for a failure");
    }

    // CBSL: flag off is a no-op (no HTTP call) that is not a failure.

    [Fact]
    public async Task Cbsl_FlagOff_IsNoOp_MakesNoHttpCall_AndIsNotAFailure()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not call ML when disabled"));

        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(),   // MarketPriceSources:Cbsl:Enabled unset => false
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        handler.Calls.Should().Be(0, "the flag-off path must not touch the ML service");
        stats.Outcome.Should().Be(IngestionRunOutcome.Skipped, "a disabled source is a SKIP, never a green success");
        var wm = wms.Peek(CbslPriceReportIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Disabled);
        wm.Status.Should().NotBe(IngestionSourceStatus.Failed, "a disabled source is never a failure");
    }

    // CBSL: the enabled happy path triggers /admin/ingest-cbsl and advances the watermark.

    [Fact]
    public async Task Cbsl_Enabled_TriggersMlPass_ReportsCounts_AndAdvancesWatermark()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(CbslPriceReportIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 7, 17));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"status":"ok","priceObservations":{"inserted":57,"updated":40,"skippedNoMarket":40,"maxObservedDate":"2026-07-21"},"heal":{"healed":0,"unresolved":12},"gaps":{"nInfo":1,"nWarning":0,"nError":0}}"""));

        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(("MarketPriceSources:Cbsl:Enabled", "true"),
                   ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        handler.Calls.Should().Be(1);
        handler.LastRequest!.RequestUri!.AbsolutePath.Should().EndWith("admin/ingest-cbsl");
        // Look-back: sinceDate = LastObservedDate - 3 (default CbslLookbackDays).
        handler.LastRequestBody.Should().Contain("2026-07-14");
        stats.Outcome.Should().Be(IngestionRunOutcome.Succeeded);
        stats.RowsInserted.Should().Be(57);
        stats.RowsSkipped.Should().Be(40);
        stats.CoveredToDate.Should().Be(new DateOnly(2026, 7, 21));
        var wm = wms.Peek(CbslPriceReportIngestionService.SourceKey)!;
        wm.LastObservedDate.Should().Be(new DateOnly(2026, 7, 21), "a confirmed success advances the resume point");
    }

    // CBSL: enabling the flag overrides the flag-off era's Disabled watermark.

    [Fact]
    public async Task Cbsl_Enabled_RunsEvenIfWatermarkWasLeftDisabled_ByTheFlagOffEra()
    {
        // The Disabled watermark is the FLAG's reflection, not an independent control: with the
        // flag on, the pass must run (otherwise the source could never start after enabling).
        var wms = new FakeWatermarkRepository();
        var disabled = IngestionWatermark.Create(CbslPriceReportIngestionService.SourceKey);
        disabled.Disable("CBSL parser not implemented — feature-flagged OFF.");
        wms.Seed(disabled);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK,
            """{"status":"ok","priceObservations":{"inserted":5,"updated":0,"skippedNoMarket":0,"maxObservedDate":"2026-07-21"}}"""));

        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(("MarketPriceSources:Cbsl:Enabled", "true"),
                   ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        handler.Calls.Should().Be(1);
        stats.Outcome.Should().Be(IngestionRunOutcome.Succeeded);
        wms.Peek(CbslPriceReportIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Ok,
            "a successful enabled pass flips the flag-off era's Disabled state to Ok");
    }

    // CBSL: a missing admin key fails loud, and failures hold the resume point.

    [Fact]
    public async Task Cbsl_Enabled_Throws_WhenAdminKeyMissing()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(("MarketPriceSources:Cbsl:Enabled", "true"), ("MlService:AdminApiKey", "")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0, "no HTTP call must be made without an admin key");
    }

    [Fact]
    public async Task Cbsl_HttpError_RecordsFailure_ButDoesNotThrow_NorAdvanceResumePoint()
    {
        var wms = new FakeWatermarkRepository();
        var seeded = IngestionWatermark.Create(CbslPriceReportIngestionService.SourceKey);
        seeded.RecordSuccess(new DateTime(2026, 7, 18, 6, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 7, 17));
        wms.Seed(seeded);

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadGateway, "duplicate gate failed"));
        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(("MarketPriceSources:Cbsl:Enabled", "true"),
                   ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed, "a 502 (e.g. the cross-source duplicate gate) is a real failure");
        var wm = wms.Peek(CbslPriceReportIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Failed);
        wm.LastObservedDate.Should().Be(new DateOnly(2026, 7, 17), "a failed pass must not advance the resume point");
    }

    [Fact]
    public async Task Cbsl_TransportException_RecordsFailure_ButDoesNotThrow()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var svc = new CbslPriceReportIngestionService(
            ClientFrom(handler),
            Config(("MarketPriceSources:Cbsl:Enabled", "true"),
                   ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed);
        wms.Peek(CbslPriceReportIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Failed);
    }

    // CBSL MACRO: disabled by default is a no-op that makes no HTTP call and is not a failure.

    [Fact]
    public async Task CbslMacro_DefaultDisabled_IsNoOp_MakesNoHttpCall_AndIsNotAFailure()
    {
        var wms = new FakeWatermarkRepository();
        // Any HTTP call in the disabled path would be a bug: throw so the test fails loudly if hit.
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("must not call ML when disabled"));

        var svc = new CbslMacroIngestionService(
            ClientFrom(handler),
            Config(),   // MacroSources:CbslMacro:Enabled unset => false
            wms,
            NullLogger<CbslMacroIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        handler.Calls.Should().Be(0, "the disabled path must not touch the Python seam");
        var wm = wms.Peek(CbslMacroIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Disabled);
        wm.Status.Should().NotBe(IngestionSourceStatus.Failed, "a disabled source is never a failure");
    }

    // CBSL MACRO: the enabled path calls the seam, sends the admin key, and records per-series watermarks
    // from perSeriesCoverage.

    [Fact]
    public async Task CbslMacro_Enabled_CallsSeam_SendsApiKey_AndRecordsPerSeriesWatermarks()
    {
        var wms = new FakeWatermarkRepository();

        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
            {"status":"ok",
             "artifactsFetched":4,"artifactsSkipped":1,
             "rowsInserted":6,"rowsUpdated":2,"rowsSkippedInvalid":0,
             "perSeriesCoverage":{"CCPI_BASE2021":3,"FOOD_INFLATION_YOY":3}}
            """));

        var svc = new CbslMacroIngestionService(
            ClientFrom(handler),
            Config(("MacroSources:CbslMacro:Enabled", "true"), ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslMacroIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        // Seam hit once with the admin key on the P3 route.
        handler.Calls.Should().Be(1);
        handler.LastRequest!.RequestUri!.ToString().Should().EndWith("admin/ingest-cbsl-macro");
        handler.LastRequest.Headers.GetValues("X-API-Key").Should().ContainSingle().Which.Should().Be("secret-key");

        // Gating watermark advanced to Ok on success.
        wms.Peek(CbslMacroIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Ok);

        // One watermark per series, keyed CBSL_MACRO_<SeriesCode>, each Ok with its row count.
        var ccpi = wms.Peek(CbslMacroIngestionService.SeriesSourcePrefix + "CCPI_BASE2021")!;
        ccpi.Status.Should().Be(IngestionSourceStatus.Ok);
        ccpi.LastMessage.Should().Contain("rows=3");
        wms.Peek(CbslMacroIngestionService.SeriesSourcePrefix + "FOOD_INFLATION_YOY")!
            .Status.Should().Be(IngestionSourceStatus.Ok);
    }

    // CBSL MACRO: enabled but with no admin key fails loud, with no HTTP call.

    [Fact]
    public async Task CbslMacro_Enabled_Throws_WhenAdminKeyMissing()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "{}"));

        var svc = new CbslMacroIngestionService(
            ClientFrom(handler),
            Config(("MacroSources:CbslMacro:Enabled", "true"), ("MlService:AdminApiKey", "")),
            wms,
            NullLogger<CbslMacroIngestionService>.Instance);

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Calls.Should().Be(0, "no HTTP call must be made without an admin key");
    }

    // CBSL MACRO: the enabled path fails safe on a transport error — records the failure, never throws.

    [Fact]
    public async Task CbslMacro_Enabled_HttpError_RecordsFailure_ButDoesNotThrow()
    {
        var wms = new FakeWatermarkRepository();
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.BadGateway, "boom"));

        var svc = new CbslMacroIngestionService(
            ClientFrom(handler),
            Config(("MacroSources:CbslMacro:Enabled", "true"), ("MlService:AdminApiKey", "secret-key")),
            wms,
            NullLogger<CbslMacroIngestionService>.Instance);

        // Must NOT throw — fail safe; the Worker's outer try/catch is a second belt.
        await svc.IngestAsync(CancellationToken.None);

        wms.Peek(CbslMacroIngestionService.SourceKey)!.Status.Should().Be(IngestionSourceStatus.Failed);
    }
}
