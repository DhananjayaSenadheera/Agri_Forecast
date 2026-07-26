using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;
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
/// The contract the admin UI's play/stop button actually rides on: GET /api/admin/ingestion/status must
/// report state="running" WHILE a pass started through the API is in flight. The FE keeps no optimistic
/// state — it swaps play for stop purely on this field — so a pass that ran without flipping the card
/// would leave the admin looking at a Play button over a live ingestion and free to start a second one.
/// <para>
/// This is a JOINED test on purpose. The write side (IngestionPassRunner, via IngestionRunAudit) and the
/// read side (GetIngestionStatusQueryHandler's "unfinished run row younger than the staleness window")
/// each pass their own unit tests while still disagreeing about when a row appears. Here one in-memory
/// table is shared by both: the runner writes into it and the real status handler reads out of it, with a
/// source held mid-flight so the assertion happens at the exact moment that matters.
/// </para>
/// </summary>
public class IngestionStatusDuringPassTests
{
    // One in-memory IngestionRuns table, written by the runner and read by the status handler. Only the
    // three status reads are implemented; the rest throw so an unexpected read cannot pass silently.
    private sealed class SharedRunTable : IIngestionRunRepository, IIngestionReadStore
    {
        private readonly object _gate = new();
        private readonly List<IngestionRun> _rows = new();

        public IReadOnlyList<IngestionRun> Snapshot()
        {
            lock (_gate) return _rows.ToList();
        }

        // Write side. The runner's audit wrapper adds the Running row before the source runs, then mutates
        // the same instance and calls UpdateAsync — so the list holds live references, exactly as a real
        // store's rows would be re-read.
        public Task AddAsync(IngestionRun run, CancellationToken ct = default)
        {
            // Honours ct like the real store (OpenAsync throws on a cancelled token before any I/O), so the
            // stopped-before-start marker really is proven to be written with CancellationToken.None.
            ct.ThrowIfCancellationRequested();
            lock (_gate) _rows.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(IngestionRun run, CancellationToken ct = default) => Task.CompletedTask;

        // Read side, mirroring the SQL store's semantics including the exclusion set.
        private IEnumerable<IngestionRun> Visible(IReadOnlyCollection<string>? excludeSources)
        {
            lock (_gate)
            {
                return excludeSources is { Count: > 0 }
                    ? _rows.Where(r => !excludeSources.Contains(r.Source)).ToList()
                    : _rows.ToList();
            }
        }

        public Task<int> GetRunCountAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => Task.FromResult(Visible(excludeSources).Count());

        public Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            var unfinished = Visible(excludeSources).Where(r => r.FinishedUtc is null).ToList();
            return Task.FromResult(unfinished.Count == 0 ? null : (DateTime?)unfinished.Max(r => r.StartedUtc));
        }

        public Task<IngestionRunHeadRow?> GetLatestRunAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            var latest = Visible(excludeSources).OrderByDescending(r => r.StartedUtc).FirstOrDefault();
            return Task.FromResult(latest is null ? null : new IngestionRunHeadRow(latest.BatchId, latest.StartedUtc));
        }

        public Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
            Guid batchId, IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionRunStatus>>(
                Visible(excludeSources).Where(r => r.BatchId == batchId).Select(r => r.Status).ToList());

        public Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default)
            => Task.FromResult<IngestionVerificationRow?>(null);

        public Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionWatermarkRow>>(new List<IngestionWatermarkRow>());

        public Task<IngestionRunsPage> GetRunsPageAsync(int page, int pageSize, string? source, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
            IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedSettings : IIngestionStatusSettings
    {
        public string ServiceAddress => "test-host";
        public int RunningStalenessMinutes => 120;
    }

    // The FIRST source of the pass, held open so the assertion lands while the pass is genuinely in flight.
    private sealed class BlockingMarketPrice : IMarketPriceIngestionService
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new IngestionRunStats(RowsInserted: 1);
        }
    }

    // The live S5 shape: the admin presses stop while DAMBULLA_DEC is finishing, DEC completes anyway and
    // commits a green row, and the cancel is only seen by the between-sources check before WEATHER.
    private sealed class StopsItselfMarketPrice(CancellationTokenSource cts) : IMarketPriceIngestionService
    {
        public Task<IngestionRunStats> IngestAsync(CancellationToken ct)
        {
            cts.Cancel();
            return Task.FromResult(new IngestionRunStats(RowsInserted: 1));
        }
    }

    private sealed class NoOpWeather : IWeatherIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }
    private sealed class NoOpEconomic : IEconomicIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }
    private sealed class NoOpNews : INewsIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }
    private sealed class NoOpHarti : IHartiBulletinIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }
    private sealed class NoOpCbsl : ICbslPriceReportIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }
    private sealed class NoOpCbslMacro : ICbslMacroIngestionService
    { public Task<IngestionRunStats> IngestAsync(CancellationToken ct) => Task.FromResult(new IngestionRunStats()); }

    // The other half of the joined contract, and the reviewer's S5 finding: what the card says AFTER a stop.
    // Live batch 9def4f2b was stopped in the gap between DAMBULLA_DEC and WEATHER, so every row it held was
    // Succeeded and this handler rolled the batch up to "succeeded" — a deliberately halted pass reported as
    // a clean night's ingestion, which is the same false-green family as the silent daily-pipeline failure.
    // A unit test on either side alone still lets that through: the runner test only sees rows, and the
    // handler test only sees statuses someone chose to hand it. Joined, the roll-up is read off exactly the
    // rows a real stop leaves behind.
    [Fact]
    public async Task Status_DoesNotReportSucceeded_ForAPassStoppedBetweenSources()
    {
        var table = new SharedRunTable();
        var hosted = new ApiHostedIngestionPasses();
        using var cts = new CancellationTokenSource();
        var runner = BuildRunner(table, new StopsItselfMarketPrice(cts));
        var batchId = Guid.NewGuid();

        await runner.RunPassAsync(batchId, cts.Token);

        var rows = table.Snapshot();
        rows.Should().HaveCount(2, "the one source that ran, plus the halt marker for the next one");
        rows.Single(r => r.Source == IngestionSources.DambullaDec).Status
            .Should().Be(IngestionRunStatus.Succeeded);
        var marker = rows.Single(r => r.Source == IngestionSources.Weather);
        marker.Status.Should().Be(IngestionRunStatus.Failed);
        marker.ErrorSummary.Should().Be(IngestionRunAudit.CancelledReason);

        var status = await ReadStatusAsync(table, hosted);
        status.State.Should().Be("stopped", "no unfinished row is left behind by a stop");
        status.LastRunStatus.Should().NotBe("succeeded",
            "THE bug: a pass the admin killed must never report a green batch");
        status.LastRunStatus.Should().Be("partial",
            "one source genuinely succeeded and one was cut off, so partial is the honest roll-up — "
            + "\"failed\" would overstate it and erase the DEC data that really did land");
    }

    private static IngestionPassRunner BuildRunner(SharedRunTable table, IMarketPriceIngestionService first)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IIngestionRunRepository>(table);
        services.AddSingleton(first);
        services.AddSingleton<IWeatherIngestionService>(new NoOpWeather());
        services.AddSingleton<IEconomicIngestionService>(new NoOpEconomic());
        services.AddSingleton<INewsIngestionService>(new NoOpNews());
        services.AddSingleton<IHartiBulletinIngestionService>(new NoOpHarti());
        services.AddSingleton<ICbslPriceReportIngestionService>(new NoOpCbsl());
        services.AddSingleton<ICbslMacroIngestionService>(new NoOpCbslMacro());

        var provider = services.BuildServiceProvider();
        return new IngestionPassRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<IngestionPassRunner>.Instance);
    }

    private static async Task<IngestionStatus_GetDto> ReadStatusAsync(
        SharedRunTable table, IApiHostedIngestionPasses hostedPasses)
        => (await new GetIngestionStatusQueryHandler(table, new FixedSettings(), hostedPasses)
            .Handle(new GetIngestionStatusQuery(), default)).Data;

    [Fact]
    public async Task Status_ReportsRunning_WhileAnApiHostedPassIsInFlight()
    {
        var table = new SharedRunTable();
        var hosted = new ApiHostedIngestionPasses();
        var firstSource = new BlockingMarketPrice();
        var runner = BuildRunner(table, firstSource);
        var batchId = Guid.NewGuid();

        // Idle: the card shows "unknown" because the runs table is empty.
        (await ReadStatusAsync(table, hosted)).State.Should().Be("unknown");

        using var handle = hosted.Begin(batchId);
        var pass = runner.RunPassAsync(batchId, handle.Token);
        await firstSource.Entered;   // the pass is now inside its first source

        var midFlight = await ReadStatusAsync(table, hosted);

        // THE contract for the FE's play/stop swap. This works because the audit wrapper commits the
        // Running row BEFORE the source body runs, so an unfinished row already exists here.
        midFlight.State.Should().Be("running");
        midFlight.CanStop.Should().BeTrue("this pass was started by this API process, so stop can cancel it");

        table.Snapshot().Should().ContainSingle()
            .Which.Source.Should().Be(IngestionSources.DambullaDec);
        table.Snapshot().Single().FinishedUtc.Should().BeNull("the row is the in-flight breadcrumb");

        firstSource.Release();
        await pass;
        handle.Dispose();

        // Every source has now finished, so no unfinished row remains and the card drops back to stopped.
        var afterwards = await ReadStatusAsync(table, hosted);
        afterwards.State.Should().Be("stopped");
        afterwards.CanStop.Should().BeFalse();
        table.Snapshot().Should().HaveCount(7);
        table.Snapshot().Should().OnlyContain(r => r.FinishedUtc != null);
    }

    // A pass running on the scheduled worker shows running but NOT stoppable, so the FE can disable Stop
    // instead of surfacing a 409 not_stoppable after the click.
    [Fact]
    public async Task Status_ReportsRunningButNotStoppable_WhenThePassIsNotApiHosted()
    {
        var table = new SharedRunTable();
        var hosted = new ApiHostedIngestionPasses();   // nothing registered here
        var firstSource = new BlockingMarketPrice();
        var runner = BuildRunner(table, firstSource);

        var pass = runner.RunPassAsync(Guid.NewGuid(), CancellationToken.None);
        await firstSource.Entered;

        var midFlight = await ReadStatusAsync(table, hosted);
        midFlight.State.Should().Be("running");
        midFlight.CanStop.Should().BeFalse(
            "the API cannot cancel a pass it did not start — saying otherwise would make Stop lie");

        firstSource.Release();
        await pass;
    }
}
