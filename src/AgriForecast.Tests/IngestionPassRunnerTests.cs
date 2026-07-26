using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services;
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
/// <para>
/// The cancellation tests also pin the honesty rule: a stopped pass leaves a Failed row carrying
/// IngestionRunAudit.CancelledReason for the source that never started — written exactly once per stop —
/// so a halted batch can never roll up to lastRunStatus="succeeded".
/// </para>
/// </summary>
public class IngestionPassRunnerTests
{
    private sealed class Recorder
    {
        public readonly List<string> Calls = new();
        public string? ThrowOn;
        public CancellationToken LastToken;

        // Simulates the admin pressing stop while this source is the one in flight. CancelDuring names the
        // source; ThrowOnCancel decides whether the source then unwinds (a source that observes the token)
        // or completes normally anyway (a source that finishes in the gap before its next ct check — the
        // live case, where DAMBULLA_DEC landed a green row and the stop fell between sources).
        public CancellationTokenSource? PassCts;
        public string? CancelDuring;
        public bool ThrowOnCancel;

        public void Note(string source, CancellationToken ct)
        {
            Calls.Add(source);
            LastToken = ct;
            if (ThrowOn == source)
                throw new InvalidOperationException($"simulated {source} failure");

            if (CancelDuring == source)
            {
                PassCts!.Cancel();
                if (ThrowOnCancel)
                    throw new OperationCanceledException(PassCts.Token);
            }
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
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Weather, ct);
            return Task.FromResult(new IngestionRunStats());
        }
    }

    private sealed class StubEconomic(Recorder r) : IEconomicIngestionService
    {
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.Economic, ct);
            return Task.FromResult(new IngestionRunStats());
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
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            r.Note(IngestionSources.CbslMacro, ct);
            return Task.FromResult(new IngestionRunStats());
        }
    }

    private sealed class FakeRunRepository : IIngestionRunRepository
    {
        public readonly List<IngestionRun> Rows = new();

        // The token the last insert was handed. The stopped-before-start marker is written precisely when
        // the pass token is already cancelled, so it must arrive as CancellationToken.None.
        public CancellationToken LastAddToken { get; private set; }

        public Task AddAsync(IngestionRun run, CancellationToken ct = default)
        {
            // HONOURS ct like the real store: SqlConnection.OpenAsync throws on a cancelled token before any
            // I/O, and a ct-blind fake cannot reproduce the write that silently never happens.
            LastAddToken = ct;
            ct.ThrowIfCancellationRequested();
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

        // The batch is not silent about it. The admin was handed this batchId at the 202, so it must not
        // vanish without a trace, and a batch with no rows at all is one edit away from rolling up green.
        var row = runs.Rows.Should().ContainSingle(
            "the halt is recorded once, on the source that was about to start").Subject;
        row.Source.Should().Be(IngestionSources.DambullaDec);
        row.Status.Should().Be(IngestionRunStatus.Failed, "Skipped would roll up inside \"succeeded\"");
        row.ErrorSummary.Should().Be(IngestionRunAudit.CancelledReason);
        row.FinishedUtc.Should().NotBeNull("a Running marker would read as a live pass for two hours");
        runs.LastAddToken.CanBeCanceled.Should().BeFalse(
            "the marker is written BECAUSE the pass token is cancelled, so it must use CancellationToken.None");
    }

    // S5, observed live on batch 9def4f2b: the stop landed after DAMBULLA_DEC had already committed its green
    // row and before WEATHER started, so the batch held nothing but Succeeded rows and the status card
    // reported lastRunStatus="succeeded" for a pass an admin had deliberately killed. The source that was
    // about to start now carries the halt.
    [Fact]
    public async Task RunPass_StoppedBetweenSources_RecordsTheHaltOnTheNextSource()
    {
        var (runner, recorder, runs, _) = Build();
        using var cts = new CancellationTokenSource();
        recorder.PassCts = cts;
        recorder.CancelDuring = IngestionSources.DambullaDec;   // stop pressed while DEC is finishing...
        recorder.ThrowOnCancel = false;                         // ...and DEC completes anyway, green

        await runner.RunPassAsync(Guid.NewGuid(), cts.Token);

        recorder.Calls.Should().Equal(
            new[] { IngestionSources.DambullaDec }, "no further source may be launched after the stop");

        runs.Rows.Should().HaveCount(2, "the source that ran, plus one marker for the one that never started");
        runs.Rows.Single(r => r.Source == IngestionSources.DambullaDec).Status
            .Should().Be(IngestionRunStatus.Succeeded, "a source that genuinely finished keeps its real outcome");

        var marker = runs.Rows.Single(r => r.Source == IngestionSources.Weather);
        marker.Status.Should().Be(IngestionRunStatus.Failed);
        marker.ErrorSummary.Should().Be(IngestionRunAudit.CancelledReason);
        runs.Rows.Should().NotContain(r => r.Source == IngestionSources.Economic,
            "only ONE marker is written, not one per un-run source");
    }

    // The other half of the same rule: when the stop is caught by the in-flight source, that source's own row
    // already says Cancelled, so the between-sources branch must NOT write a second marker — one stop is one
    // row, and a marker for WEATHER would invent a halt nobody was waiting on.
    [Fact]
    public async Task RunPass_StoppedMidSource_DoesNotRecordTheHaltTwice()
    {
        var (runner, recorder, runs, _) = Build();
        using var cts = new CancellationTokenSource();
        recorder.PassCts = cts;
        recorder.CancelDuring = IngestionSources.DambullaDec;
        recorder.ThrowOnCancel = true;   // DEC observes the token and unwinds

        await runner.RunPassAsync(Guid.NewGuid(), cts.Token);

        var row = runs.Rows.Should().ContainSingle().Subject;
        row.Source.Should().Be(IngestionSources.DambullaDec);
        row.Status.Should().Be(IngestionRunStatus.Failed);
        row.ErrorSummary.Should().Be(IngestionRunAudit.CancelledReason);
        runs.Rows.Should().NotContain(r => r.Source == IngestionSources.Weather,
            "the halt is already on the record — no duplicate marker for the next source");
    }

    // The marker is a stop-only artefact: a pass nobody interrupted writes exactly the seven source rows.
    [Fact]
    public async Task RunPass_NormalCompletion_WritesNoCancellationMarker()
    {
        var (runner, _, runs, _) = Build();

        await runner.RunPassAsync(Guid.NewGuid(), CancellationToken.None);

        runs.Rows.Should().HaveCount(7);
        runs.Rows.Should().NotContain(r => r.ErrorSummary == IngestionRunAudit.CancelledReason);
        runs.Rows.Should().NotContain(r => r.Status == IngestionRunStatus.Failed);
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
