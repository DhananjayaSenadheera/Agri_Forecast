using System.Net;
using System.Text;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Services.CbslIngestion;
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
    // ── Test doubles ─────────────────────────────────────────────────────────────────────────

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

    // ── HARTI: happy path advances the watermark ─────────────────────────────────────────────

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

    // ── HARTI: look-back window is configurable ──────────────────────────────────────────────

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

    // ── HARTI: null watermark => full backfill (sinceDate omitted/null) ───────────────────────

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

    // ── HARTI: missing key fails loud ────────────────────────────────────────────────────────

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

    // ── HARTI: transport/HTTP failure holds the resume point (never throws to the Worker) ─────

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

    // ── HARTI: a Disabled watermark is a no-op (no HTTP, not a failure) ──────────────────────

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

    // ── CBSL: disabled by default is a no-op that is NOT a failure ───────────────────────────

    [Fact]
    public async Task Cbsl_DefaultDisabled_IsNoOp_DoesNotCallClient_AndIsNotAFailure()
    {
        var wms = new FakeWatermarkRepository();
        var clientCalled = false;
        var client = new StubCbslClient(() => { clientCalled = true; });

        var svc = new CbslPriceReportIngestionService(
            client,
            Config(),   // MarketPriceSources:Cbsl:Enabled unset => false
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        await svc.IngestAsync(CancellationToken.None);

        clientCalled.Should().BeFalse("the disabled path must not touch the (unimplemented) parser");
        var wm = wms.Peek(CbslPriceReportIngestionService.SourceKey)!;
        wm.Status.Should().Be(IngestionSourceStatus.Disabled);
        wm.Status.Should().NotBe(IngestionSourceStatus.Failed, "a disabled source is never a failure");
    }

    // ── CBSL: enabled path fails LOUD (never a silent fake success) ──────────────────────────

    [Fact]
    public async Task Cbsl_Enabled_CallsClient_AndSurfacesNotSupportedLoudly()
    {
        var wms = new FakeWatermarkRepository();
        // The real client throws NotSupported; use it to prove the enabled path does not swallow.
        var client = new AgriForecast.Infrastructure.ExternalSources.Interfaces.CbslPriceReportClient(
            new HttpClient(), NullLogger<AgriForecast.Infrastructure.ExternalSources.Interfaces.CbslPriceReportClient>.Instance);

        var svc = new CbslPriceReportIngestionService(
            client,
            Config(("MarketPriceSources:Cbsl:Enabled", "true")),
            wms,
            NullLogger<CbslPriceReportIngestionService>.Instance);

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>(
            "the enabled path must fail loudly until a real parser exists, never fake a success");
    }

    private sealed class StubCbslClient : ICbslPriceReportClient
    {
        private readonly Action _onCall;
        public StubCbslClient(Action onCall) => _onCall = onCall;

        public Task<IReadOnlyList<CbslDailyPriceReportDto>> GetDailyPriceReportAsync(
            DateOnly? sinceDate, CancellationToken ct)
        {
            _onCall();
            return Task.FromResult<IReadOnlyList<CbslDailyPriceReportDto>>(Array.Empty<CbslDailyPriceReportDto>());
        }
    }
}
