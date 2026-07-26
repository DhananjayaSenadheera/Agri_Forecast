using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services.CbslIngestion;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.HartiIngestion;
using AgriForecast.Infrastructure.Services.IngestionControl;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.NewsIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// IngestionPassRunner — the single pass, lifted out of the Ingestion Worker so the scheduled worker and
/// the admin start button run the same code. The seven sources are stubbed and the run store is in-memory,
/// so what is actually pinned is the ORCHESTRATION:
/// the source set and their order; the shared batchId; per-source fail isolation; and the between-sources
/// cancellation check that makes the admin stop button mean something.
/// </summary>
public class IngestionPassRunnerTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public string? ThrowOn;
        public CancellationToken LastToken;

        public void Note(string source, CancellationToken ct)
        {
            Calls.Add(source);
            LastToken = ct;
            if (ThrowOn == source)
                throw new InvalidOperationException($"simulated {source} failure");
        }
    }

    private sealed class StubMarketPrice(Recorder r) : IMarketPriceIngestionService
    {
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.DambullaDec, ct);
            return Task.FromResult(new IngestionRunStats(RowsInserted: 5));
        }
    }

    private sealed class StubWeather(Recorder r) : IWeatherIngestionService
    {
        public Task IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Weather, ct);
            return Task.CompletedTask;
        }
    }

    private sealed class StubEconomic(Recorder r) : IEconomicIngestionService
    {
        public Task IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Economic, ct);
            return Task.CompletedTask;
        }
    }

    private sealed class StubNews(Recorder r) : INewsIngestionService
    {
        public IngestionRunStats Result = new();

        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.News, ct);
            return Task.FromResult(Result);
        }
    }

    private sealed class StubHarti(Recorder r) : IHartiBulletinIngestionService
    {
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Harti, ct);
            return Task.FromResult(new IngestionRunStats());
        }
    }

    private sealed class StubCbsl(Recorder r) : ICbslPriceReportIngestionService
    {
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Cbsl, ct);
            return Task.FromResult(new IngestionRunStats(Outcome: IngestionRunOutcome.Skipped));
        }
    }

    private sealed class StubCbslMacro(Recorder r) : ICbslMacroIngestionService
    {
        public Task IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.CbslMacro, ct);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRunRepository : IIngestionRunRepository
    {
        public readonly List<IngestionRun> Rows = new();

        public Task AddAsync(IngestionRun run, CancellationToken ct = default)
        {
            Rows.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(IngestionRun run, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static (IIngestionPassRunner Runner, Recorder Recorder, FakeRunRepository Runs, StubNews News) Build()
    {
        var recorder = new Recorder();
        var runs = new FakeRunRepository();
        var news = new StubNews(recorder);

        var services = new ServiceCollection();
        services.AddSingleton<IIngestionRunRepository>(runs);
        services.AddSingleton<IMarketPriceIngestionService>(new StubMarketPrice(recorder));
        services.AddSingleton<IWeatherIngestionService>(new StubWeather(recorder));
        services.AddSingleton<IEconomicIngestionService>(new StubEconomic(recorder));
        services.AddSingleton<INewsIngestionService>(news);
        services.AddSingleton<IHartiBulletinIngestionService>(new StubHarti(recorder));
        services.AddSingleton<ICbslPriceReportIngestionService>(new StubCbsl(recorder));
        services.AddSingleton<ICbslMacroIngestionService>(new StubCbslMacro(recorder));

        var provider = services.BuildServiceProvider();
        var runner = new IngestionPassRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IngestionPassRunner>.Instance);

        return (runner, recorder, runs, news);
    }

    [Fact]
    public async Task RunPass_RunsAllSevenSources_InTheWorkersOrder()
    {
        var (runner, recorder, _, _) = Build();

        await runner.RunPassAsync(Guid.NewGuid(), CancellationToken.None);

        // The order is the one the Worker used before the lift; DAMBULLA_DEC first because it is the
        // primary price source, the two feature-flagged CBSL sources last.
        recorder.Calls.Should().Equal(
            IngestionSources.DambullaDec,
            IngestionSources.Weather,
            IngestionSources.Economic,
            IngestionSources.News,
            IngestionSources.Harti,
            IngestionSources.Cbsl,
            IngestionSources.CbslMacro);
    }

    [Fact]
    public async Task RunPass_GivesEverySourceRowTheSameBatchId()
    {
        var (runner, _, runs, _) = Build();
        var batchId = Guid.NewGuid();

        await runner.RunPassAsync(batchId, CancellationToken.None);

        runs.Rows.Should().HaveCount(7);
        runs.Rows.Select(r => r.BatchId).Distinct().Should().ContainSingle().Which.Should().Be(batchId);
    }

    [Fact]
    public async Task RunPass_IsolatesAFailingSource_AndKeepsGoing()
    {
        var (runner, recorder, runs, _) = Build();
        recorder.ThrowOn = IngestionSources.Economic;

        await runner.RunPassAsync(Guid.NewGuid(), CancellationToken.None);

        recorder.Calls.Should().HaveCount(7, "one bad source must never abort the pass");
        runs.Rows.Single(r => r.Source == IngestionSources.Economic).Status
            .Should().Be(IngestionRunStatus.Failed);
        runs.Rows.Single(r => r.Source == IngestionSources.Weather).Status
            .Should().Be(IngestionRunStatus.Succeeded);
    }

    // The NEWS false-green fix, seen from the runner: a fail-safe source that reports Outcome=Failed gets a
    // red row even though its body returned normally.
    [Fact]
    public async Task RunPass_HonoursAFailSafeSourcesFailedOutcome()
    {
        var (runner, _, runs, news) = Build();
        news.Result = new IngestionRunStats(
            Outcome: IngestionRunOutcome.Failed, FailureReason: "ML service unreachable");

        await runner.RunPassAsync(Guid.NewGuid(), CancellationToken.None);

        var row = runs.Rows.Single(r => r.Source == IngestionSources.News);
        row.Status.Should().Be(IngestionRunStatus.Failed);
        row.ErrorSummary.Should().Contain("ML service unreachable");
    }

    // What the admin stop button buys: the pass stops launching further sources. It is checked BETWEEN
    // sources rather than tearing one down mid-write.
    [Fact]
    public async Task RunPass_StopsLaunchingSources_OnceCancelled()
    {
        var (runner, recorder, runs, _) = Build();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await runner.RunPassAsync(Guid.NewGuid(), cts.Token);

        recorder.Calls.Should().BeEmpty("cancellation before the first source means no source runs");
        runs.Rows.Should().BeEmpty("no run row is minted for a source that never started");
    }

    [Fact]
    public async Task RunPass_PassesTheCancellationTokenToEachSource()
    {
        var (runner, recorder, _, _) = Build();
        using var cts = new CancellationTokenSource();

        await runner.RunPassAsync(Guid.NewGuid(), cts.Token);

        // A source must be able to abandon its own long HTTP call when the admin stops the pass.
        Assert.Equal(cts.Token, recorder.LastToken);
    }
}
