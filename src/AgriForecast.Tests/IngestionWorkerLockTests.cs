using AgriForecast.Application.Services;
using AgriForecast.Ingestion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// The scheduled half of the cross-process mutex, asserted on the REAL Worker.
/// <para>
/// The trap this closes: the application lock had exactly one caller — the API's admin start handler —
/// while four comments described it as the cross-process mutex. A lock only one of two callers takes is
/// not a mutex at all, so the 21:00 CronJob could run a full pass straight over an admin-started one,
/// double-fetching every source and interleaving watermark writes. The Worker must take the same lease,
/// and must SKIP (not queue, not retry) when it cannot.
/// </para>
/// Run in the Worker's RunOnce mode — the mode the CronJob actually uses — so ExecuteAsync performs one
/// pass and stops instead of parking on its 24-hour delay.
/// </summary>
public class IngestionWorkerLockTests
{
    private sealed class FakePassLock : IIngestionPassLock
    {
        private readonly bool _grant;
        public int AcquireAttempts { get; private set; }
        public int Releases { get; private set; }

        public FakePassLock(bool grant) => _grant = grant;

        public Task<IIngestionPassLease?> TryAcquireAsync(CancellationToken ct = default)
        {
            AcquireAttempts++;
            return Task.FromResult<IIngestionPassLease?>(_grant ? new Lease(this) : null);
        }

        private sealed class Lease : IIngestionPassLease
        {
            private readonly FakePassLock _owner;
            public Lease(FakePassLock owner) => _owner = owner;

            public ValueTask DisposeAsync()
            {
                _owner.Releases++;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingRunner : IIngestionPassRunner
    {
        public int Passes { get; private set; }
        public Guid? LastBatchId { get; private set; }

        public Task RunPassAsync(Guid batchId, CancellationToken ct)
        {
            Passes++;
            LastBatchId = batchId;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => StopRequested = true;
    }

    // RunOnce so ExecuteAsync runs a single pass and returns rather than looping on Task.Delay(24h).
    private static IConfiguration RunOnceConfig(string? pinnedBatchId = null)
    {
        var values = new Dictionary<string, string?> { ["Ingestion:RunOnce"] = "true" };
        if (pinnedBatchId is not null)
            values["Ingestion:BatchId"] = pinnedBatchId;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<(RecordingRunner Runner, FakePassLock Lock, FakeLifetime Lifetime)> RunWorkerAsync(
        bool lockGranted, string? pinnedBatchId = null)
    {
        var runner = new RecordingRunner();
        var passLock = new FakePassLock(lockGranted);
        var lifetime = new FakeLifetime();

        var worker = new Worker(
            NullLogger<Worker>.Instance, runner, passLock, RunOnceConfig(pinnedBatchId), lifetime);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        return (runner, passLock, lifetime);
    }

    [Fact]
    public async Task Worker_AcquiresThePassLock_BeforeRunningAScheduledPass()
    {
        var (runner, passLock, _) = await RunWorkerAsync(lockGranted: true);

        passLock.AcquireAttempts.Should().Be(1,
            "the scheduled pass must take the same cross-process lock the admin start button takes");
        runner.Passes.Should().Be(1);
    }

    [Fact]
    public async Task Worker_SkipsThePass_WhenAnotherHostHoldsTheLock()
    {
        var (runner, passLock, lifetime) = await RunWorkerAsync(lockGranted: false);

        passLock.AcquireAttempts.Should().Be(1);
        runner.Passes.Should().Be(0,
            "an admin-started pass is already covering this data; a second concurrent pass would "
            + "double-fetch every source and interleave watermark writes");

        // Skip, do NOT queue or retry: DEC re-fetches full history every pass, so whatever this run would
        // have picked up lands on the next one, and the CronJob's startingDeadlineSeconds covers genuine
        // missed schedules. The host still exits cleanly rather than hanging on a retry loop.
        lifetime.StopRequested.Should().BeTrue("a skipped RunOnce pass must still exit the process cleanly");
    }

    [Fact]
    public async Task Worker_ReleasesTheLease_WhenThePassCompletes()
    {
        var (_, passLock, _) = await RunWorkerAsync(lockGranted: true);

        passLock.Releases.Should().Be(1,
            "an undisposed lease would wedge every later pass until the SQL session died");
    }

    [Fact]
    public async Task Worker_ThreadsTheConfiguredBatchId_ThroughToThePass()
    {
        // The CronJob pins one batchId across every step of the nightly pipeline.
        var pinned = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00002222");

        var (runner, _, _) = await RunWorkerAsync(lockGranted: true, pinnedBatchId: pinned.ToString());

        runner.LastBatchId.Should().Be(pinned);
    }
}
