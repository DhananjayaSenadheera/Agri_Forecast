using System.Net;
using System.Text;
using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Services;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// The false-green sweep. NEWS was not a one-off: WEATHER, ECONOMIC and CBSL_MACRO each had the identical
/// shape — catch the failure, log it, <c>return</c> void — so the audit wrapper saw a body that completed
/// normally and wrote a green Succeeded run row for a pass that ingested nothing.
/// <para>
/// Every test here asserts the pair that makes the fix trustworthy: a genuine failure reaches the run row
/// as Failed, AND the legitimately-empty pass stays green. A source that cries wolf on a quiet day is just
/// the same bug pointing the other way, and would train the admin to ignore red.
/// </para>
/// Each source is driven through the REAL IngestionRunAudit wrapper, so what is asserted is the row the
/// admin ingestion page renders, not merely the record the service returned.
/// </summary>
public class SourceOutcomeHonestyTests
{
    // Shared fakes.

    private sealed class FakeRunRepository : IIngestionRunRepository
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

    private sealed class FakeUnitOfWork : IUnitofWorkRepository
    {
        public Task CommitAsync() => Task.CompletedTask;
        public void Dispose() { }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    // Runs a source through the real audit wrapper and returns the row the admin page would show.
    private static async Task<IngestionRun> RowFor(
        string source, Func<CancellationToken, Task<IngestionRunStats>> body)
    {
        var runs = new FakeRunRepository();
        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), source,
            async ct => (IngestionRunStats?)await body(ct),
            CancellationToken.None);
        return runs.Single;
    }

    // WEATHER.

    private sealed class StubWeatherClient : IWeatherClient
    {
        public Exception? Throw;
        public IReadOnlyList<DailyWeather> Days = Array.Empty<DailyWeather>();

        public Task<IReadOnlyList<DailyWeather>> GetDailyAsync(
            double lat, double lon, DateOnly from, DateOnly to, CancellationToken ct)
            => Throw is not null
                ? Task.FromException<IReadOnlyList<DailyWeather>>(Throw)
                : Task.FromResult(Days);
    }

    private sealed class FakeWeatherRepository : IWeatherRecordRepository
    {
        public readonly List<WeatherRecord> Added = new();
        public List<WeatherRecord> Existing = new();

        public Task AddAsync(WeatherRecord record, CancellationToken ct = default)
        {
            Added.Add(record);
            return Task.CompletedTask;
        }
        public Task<WeatherRecord?> GetByMonthAsync(DateTime month, CancellationToken ct = default)
            => Task.FromResult<WeatherRecord?>(null);
        public Task<List<WeatherRecord>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
            => Task.FromResult(Existing);
    }

    private static WeatherIngestionService WeatherService(
        StubWeatherClient client, FakeWeatherRepository repo, string startDate = "2025-02-01")
        => new(client, Config(("WeatherSource:StartDate", startDate)),
            NullLogger<WeatherIngestionService>.Instance, repo, new FakeUnitOfWork());

    [Fact]
    public async Task Weather_ProviderFetchFails_MarksTheRunRowFailed()
    {
        var client = new StubWeatherClient { Throw = new HttpRequestException("open-meteo unreachable") };
        var svc = WeatherService(client, new FakeWeatherRepository());

        var row = await RowFor("WEATHER", svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Failed,
            "the false-green bug: a failed provider fetch used to leave this row Succeeded");
        row.ErrorSummary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Weather_NothingNewToIngest_StaysSucceeded()
    {
        // A month already stored: the fetch works, there is simply nothing to add.
        var stored = new WeatherRecord(new DateTime(2025, 2, 1), 28.4m, 120.5m);
        var repo = new FakeWeatherRepository { Existing = new List<WeatherRecord> { stored } };
        var client = new StubWeatherClient
        {
            Days = new[] { new DailyWeather(new DateOnly(2025, 2, 1), 28.4m, 4.0m) }
        };

        var row = await RowFor("WEATHER", WeatherService(client, repo).IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Succeeded,
            "a pass that ran and found every month already stored is a real success");
    }

    [Fact]
    public async Task Weather_NoCompleteMonthYet_StaysSucceeded()
    {
        // StartDate in the future: no fully-elapsed month exists, so the source correctly does nothing.
        var future = DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(6).ToString("yyyy-MM-dd");
        var svc = WeatherService(new StubWeatherClient(), new FakeWeatherRepository(), future);

        var row = await RowFor("WEATHER", svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Succeeded);
    }

    // ECONOMIC.

    private sealed class StubFxClient : IEconomicDataClient
    {
        public FxRate? Latest;
        public Task<FxRate?> GetLatestUsdToLkrAsync(CancellationToken ct) => Task.FromResult(Latest);
    }

    private sealed class StubFxHistoricalClient : IFxHistoricalClient
    {
        public Task<IReadOnlyList<FxRate>> GetRatesForMonthStartsAsync(
            IReadOnlyList<DateOnly> monthStarts, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FxRate>>(Array.Empty<FxRate>());
    }

    private sealed class FakeEconomicRepository : IEconomicIndicatorRepository
    {
        public readonly List<EconomicIndicator> Added = new();
        public bool TodayAlreadyPresent;

        public Task AddAsync(EconomicIndicator indicator, CancellationToken ct = default)
        {
            Added.Add(indicator);
            return Task.CompletedTask;
        }
        public Task<bool> ExistsAsync(DateTime date, string indicatorCode, CancellationToken ct = default)
            => Task.FromResult(TodayAlreadyPresent);
        public Task<List<EconomicIndicator>> GetRangeAsync(
            DateTime from, DateTime to, string indicatorCode, CancellationToken ct = default)
            => Task.FromResult(new List<EconomicIndicator>());
    }

    private static EconomicIngestionService EconomicService(StubFxClient client, FakeEconomicRepository repo)
        => new(client, new StubFxHistoricalClient(),
            NullLogger<EconomicIngestionService>.Instance, repo, new FakeUnitOfWork());

    [Fact]
    public async Task Economic_ProviderReturnsNoRate_MarksTheRunRowFailed()
    {
        var svc = EconomicService(new StubFxClient { Latest = null }, new FakeEconomicRepository());

        var row = await RowFor("ECONOMIC", svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Failed,
            "no FX rate captured means the pass ingested nothing — it used to render green");
        row.ErrorSummary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Economic_RateCaptured_MarksTheRunRowSucceeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var svc = EconomicService(
            new StubFxClient { Latest = new FxRate(today, 305.25m) }, new FakeEconomicRepository());

        var row = await RowFor("ECONOMIC", svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Succeeded);
        row.RowsInserted.Should().Be(1);
    }

    [Fact]
    public async Task Economic_TodaysRateAlreadyPresent_StaysSucceeded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var repo = new FakeEconomicRepository { TodayAlreadyPresent = true };
        var svc = EconomicService(new StubFxClient { Latest = new FxRate(today, 305.25m) }, repo);

        var row = await RowFor("ECONOMIC", svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Succeeded,
            "idempotency is not failure — a second pass on the same day is a real success");
        repo.Added.Should().BeEmpty();
    }

    // CBSL_MACRO.

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _toThrow;
        public ThrowingHttpMessageHandler(Exception toThrow) => _toThrow = toThrow;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_toThrow);
    }

    private sealed class FakeWatermarkRepository : IIngestionWatermarkRepository
    {
        private readonly Dictionary<string, IngestionWatermark> _store = new();

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

        public Task SaveChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
        public IngestionWatermark? Peek(string source) => _store.GetValueOrDefault(source);
    }

    private static CbslMacroIngestionService MacroService(
        HttpMessageHandler handler, FakeWatermarkRepository watermarks, bool enabled)
        => new(
            new HttpClient(handler) { BaseAddress = new Uri("http://ml.test/") },
            Config(("MacroSources:CbslMacro:Enabled", enabled ? "true" : "false"),
                   ("MlService:AdminApiKey", "secret-key")),
            watermarks,
            NullLogger<CbslMacroIngestionService>.Instance);

    [Fact]
    public async Task CbslMacro_TransportFailure_MarksTheRunRowFailed()
    {
        var watermarks = new FakeWatermarkRepository();
        var svc = MacroService(
            new ThrowingHttpMessageHandler(new HttpRequestException("ml-serving unreachable")),
            watermarks, enabled: true);

        var row = await RowFor(CbslMacroIngestionService.SourceKey, svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Failed);
        // The watermark already went red on this path; the run row is what used to lie.
        watermarks.Peek(CbslMacroIngestionService.SourceKey)!.Status
            .Should().Be(IngestionSourceStatus.Failed, "watermark and run row must now agree");
    }

    [Fact]
    public async Task CbslMacro_NonSuccessStatus_MarksTheRunRowFailed()
    {
        var svc = MacroService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("{\"detail\":\"boom\"}", Encoding.UTF8, "application/json")
            }),
            new FakeWatermarkRepository(), enabled: true);

        var row = await RowFor(CbslMacroIngestionService.SourceKey, svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Failed);
        row.ErrorSummary.Should().Contain("502");
    }

    // The feature flag is off by default, and that is a deliberate no-op — Skipped, never Failed. Getting
    // this wrong would paint the ingestion card red every night for a source nobody enabled.
    [Fact]
    public async Task CbslMacro_FeatureFlaggedOff_IsSkipped_NotFailedAndNotSucceeded()
    {
        var watermarks = new FakeWatermarkRepository();
        var svc = MacroService(
            new ThrowingHttpMessageHandler(new HttpRequestException("must never be called")),
            watermarks, enabled: false);

        var row = await RowFor(CbslMacroIngestionService.SourceKey, svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Skipped);
        row.ErrorSummary.Should().BeNull();
        watermarks.Peek(CbslMacroIngestionService.SourceKey)!.Status
            .Should().Be(IngestionSourceStatus.Disabled);
    }

    [Fact]
    public async Task CbslMacro_Success_MarksTheRunRowSucceeded()
    {
        var svc = MacroService(
            new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {"status":"ok","artifactsFetched":2,"artifactsSkipped":0,
                     "rowsInserted":14,"rowsUpdated":0,"rowsSkippedInvalid":1,
                     "perSeriesCoverage":{"CCPI":7,"MEI":7}}
                    """, Encoding.UTF8, "application/json")
            }),
            new FakeWatermarkRepository(), enabled: true);

        var row = await RowFor(CbslMacroIngestionService.SourceKey, svc.IngestAsync);

        row.Status.Should().Be(IngestionRunStatus.Succeeded);
        row.RowsInserted.Should().Be(14);
    }
}
