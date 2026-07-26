using System.Net;
using System.Text;
using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services;
using AgriForecast.Infrastructure.Services.NewsIngestion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// The NEWS false-green regression. NewsIngestionService is deliberately fail-SAFE — a dead ML service must
/// not abort the other six sources — but it used to be fail-SILENT: every error path logged a warning and
/// returned void, so IngestionRunAudit saw a body that completed normally and wrote a green Succeeded run
/// row. The admin ingestion card therefore reported healthy news ingestion on days nothing was ingested.
/// <para>
/// These tests pin both halves of the fix: a real failure reaches the run row as Failed, and a pass that
/// genuinely ran and found nothing new stays Succeeded. The last test drives the service through the real
/// audit wrapper, so it proves the ROW, not just the returned record.
/// </para>
/// </summary>
public class NewsIngestionServiceTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public int Calls { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_responder(request));
        }
    }

    // Throws the way a real transport failure does: the ML service is down or unreachable.
    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _toThrow;
        public ThrowingHttpMessageHandler(Exception toThrow) => _toThrow = toThrow;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_toThrow);
    }

    private sealed class FakeIngestionRunRepository : IIngestionRunRepository
    {
        public readonly List<IngestionRun> Rows = new();

        public Task AddAsync(IngestionRun run, CancellationToken ct = default)
        {
            Rows.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(IngestionRun run, CancellationToken ct = default) => Task.CompletedTask;

        public IngestionRun Single => Rows.Single();
    }

    private static IConfiguration Config(string? apiKey = "secret-key")
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MlService:AdminApiKey"] = apiKey })
            .Build();

    private static NewsIngestionService Service(HttpMessageHandler handler, string? apiKey = "secret-key")
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://ml.test/") },
            Config(apiKey),
            NullLogger<NewsIngestionService>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode code, string body)
        => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string OkBody = """
        {"status":"ok",
         "ingest":{"inserted":7,"dupSkipped":3},
         "score":{"articlesScored":10,"rowsWritten":2}}
        """;

    [Fact]
    public async Task Success_ReportsSucceeded_AndMapsCounts()
    {
        var svc = Service(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, OkBody)));

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Succeeded);
        stats.RowsInserted.Should().Be(7);
        stats.RowsSkipped.Should().Be(3);
        stats.FailureReason.Should().BeNull();
    }

    // The other half of the fix: not everything quiet is broken.
    [Fact]
    public async Task SuccessWithNoNewArticles_StaysSucceeded()
    {
        var svc = Service(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
            {"status":"ok","ingest":{"inserted":0,"dupSkipped":0},"score":{"articlesScored":0,"rowsWritten":0}}
            """)));

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Succeeded,
            "a pass that ran and found no new articles is a real success, not a failure");
        stats.RowsInserted.Should().Be(0);
    }

    [Fact]
    public async Task ServiceDown_ReportsFailed_WithoutThrowing()
    {
        var svc = Service(new ThrowingHttpMessageHandler(new HttpRequestException("connection refused")));

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed,
            "this is the exact case that used to write a green run row");
        stats.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Timeout_ReportsFailed()
    {
        var svc = Service(new ThrowingHttpMessageHandler(new TaskCanceledException("timed out")));

        (await svc.IngestAsync(CancellationToken.None)).Outcome.Should().Be(IngestionRunOutcome.Failed);
    }

    // The Python route raises 502/503 when the ingest or scoring step fails, so a non-2xx is a genuine
    // pipeline failure.
    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task NonSuccessStatus_ReportsFailed_NamingTheStatus(HttpStatusCode code)
    {
        var svc = Service(new StubHttpMessageHandler(_ => Json(code, """{"detail":"boom"}""")));

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed);
        stats.FailureReason.Should().Contain(((int)code).ToString());
    }

    [Fact]
    public async Task UnparseableBody_ReportsFailed()
    {
        var svc = Service(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, "not json at all")));

        (await svc.IngestAsync(CancellationToken.None)).Outcome.Should().Be(IngestionRunOutcome.Failed);
    }

    // A 200 whose body says the pipeline did not finish cleanly must not be read as success just because
    // the HTTP status was green.
    [Fact]
    public async Task TwoHundredWithNonOkStatusField_ReportsFailed()
    {
        var svc = Service(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, """
            {"status":"error","ingest":{"inserted":0,"dupSkipped":0}}
            """)));

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.Outcome.Should().Be(IngestionRunOutcome.Failed);
        stats.FailureReason.Should().Contain("error");
    }

    // End-to-end through the real audit wrapper: a swallowed source error must land on the ROW as Failed.
    [Fact]
    public async Task SwallowedTransportError_MarksTheRunRowFailed()
    {
        var runs = new FakeIngestionRunRepository();
        var svc = Service(new ThrowingHttpMessageHandler(new HttpRequestException("connection refused")));

        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "NEWS",
            async ct => (IngestionRunStats?)await svc.IngestAsync(ct),
            CancellationToken.None);

        var row = runs.Single;
        row.Source.Should().Be("NEWS");
        row.Status.Should().Be(IngestionRunStatus.Failed,
            "the false-green bug: this row used to be Succeeded");
        row.ErrorSummary.Should().NotBeNullOrWhiteSpace();
    }

    // A THROWN source error (the missing-admin-key configuration guard) must also land as Failed. That path
    // was already correct; it is pinned here so the return-type change did not quietly convert a throw into
    // a green row.
    [Fact]
    public async Task ThrownConfigurationError_MarksTheRunRowFailed()
    {
        var runs = new FakeIngestionRunRepository();
        var handler = new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, OkBody));
        var svc = Service(handler, apiKey: null);

        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "NEWS",
            async ct => (IngestionRunStats?)await svc.IngestAsync(ct),
            CancellationToken.None);

        runs.Single.Status.Should().Be(IngestionRunStatus.Failed);
        runs.Single.ErrorSummary.Should().NotBeNullOrWhiteSpace();
        handler.Calls.Should().Be(0, "the key is checked before any request leaves the process");
    }

    [Fact]
    public async Task SuccessfulPass_MarksTheRunRowSucceeded_WithCounts()
    {
        var runs = new FakeIngestionRunRepository();
        var svc = Service(new StubHttpMessageHandler(_ => Json(HttpStatusCode.OK, OkBody)));

        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "NEWS",
            async ct => (IngestionRunStats?)await svc.IngestAsync(ct),
            CancellationToken.None);

        runs.Single.Status.Should().Be(IngestionRunStatus.Succeeded);
        runs.Single.RowsInserted.Should().Be(7);
        runs.Single.RowsSkipped.Should().Be(3);
    }
}
