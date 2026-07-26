using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using AgriForecast.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Behavioural tests for IngestionRunAudit — the per-source run-row lifecycle wrapper the Worker
/// uses. The load-bearing guarantees: (1) a Running row is committed BEFORE the source runs and
/// transitioned to Succeeded/Failed/Skipped after; (2) an audit-write failure can NEVER break the
/// pass; (3) BatchId comes from config when valid, else is generated once; (4) a fail-safe source's
/// Outcome (Failed/Skipped) is honoured so a real failure never renders as a green Succeeded row.
/// The run store is an in-memory fake so transitions + write counts can be asserted directly.
/// </summary>
public class IngestionRunAuditTests
{
    private sealed class FakeIngestionRunRepository : IIngestionRunRepository
    {
        public readonly List<IngestionRun> Added = new();
        public int AddCount { get; private set; }
        public int UpdateCount { get; private set; }

        // 1 => throw on AddAsync (the Running-insert commit); 2 => throw on UpdateAsync (the terminal
        // commit). 0 = never throw. Simulates an audit-write DB failure at each phase.
        public int ThrowOnSaveIndex { get; init; }

        // The token each write was actually handed, so a test can prove the terminal write does not
        // observe the pass's (cancelled) token.
        public CancellationToken LastUpdateToken { get; private set; }

        public Task AddAsync(IngestionRun run, CancellationToken ct = default)
        {
            // HONOURS ct, like the real store. A fake that ignores it is exactly why the stranded-row bug
            // survived the first round of tests: SqlConnection.OpenAsync throws on a cancelled token
            // before any I/O, so a ct-blind fake can never reproduce it.
            ct.ThrowIfCancellationRequested();
            AddCount++;
            Added.Add(run); // mint-captured even if the "commit" then fails, so tests can inspect it
            if (ThrowOnSaveIndex == 1)
                throw new InvalidOperationException("simulated DB failure on the Running-row insert");
            return Task.CompletedTask;
        }

        public Task UpdateAsync(IngestionRun run, CancellationToken ct = default)
        {
            LastUpdateToken = ct;
            ct.ThrowIfCancellationRequested();
            UpdateCount++;
            if (ThrowOnSaveIndex == 2)
                throw new InvalidOperationException("simulated DB failure on the terminal transition");
            return Task.CompletedTask;
        }

        public IngestionRun? Single => Added.SingleOrDefault();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    // ResolveBatchId.

    [Fact]
    public void ResolveBatchId_UsesConfiguredGuid_WhenPresentAndValid()
    {
        var pinned = Guid.Parse("aaaabbbb-cccc-dddd-eeee-ffff00001111");
        var batch = IngestionRunAudit.ResolveBatchId(Config(("Ingestion:BatchId", pinned.ToString())));

        batch.Should().Be(pinned, "a valid configured BatchId must be reused so an orchestrator can group a pass");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")] // Guid.Empty is not a usable batch id
    public void ResolveBatchId_GeneratesFresh_WhenAbsentOrInvalid(string? configured)
    {
        var pairs = configured is null
            ? Array.Empty<(string, string?)>()
            : new[] { ("Ingestion:BatchId", (string?)configured) };

        var batch = IngestionRunAudit.ResolveBatchId(Config(pairs));

        batch.Should().NotBe(Guid.Empty, "an absent/invalid BatchId must fall back to a generated GUID");
    }

    // Lifecycle: Running -> Succeeded with counts.

    [Fact]
    public async Task RunTracked_Success_WithStats_RecordsRunningThenSucceededWithCounts()
    {
        var runs = new FakeIngestionRunRepository();
        var batch = Guid.NewGuid();
        var stats = new IngestionRunStats(RowsInserted: 40, RowsSkipped: 55, DistinctCrops: 12);

        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, batch, "DAMBULLA_DEC",
            _ => Task.FromResult<IngestionRunStats?>(stats), CancellationToken.None);

        var run = runs.Single!;
        run.BatchId.Should().Be(batch);
        run.Source.Should().Be("DAMBULLA_DEC");
        run.Status.Should().Be(IngestionRunStatus.Succeeded);
        run.FinishedUtc.Should().NotBeNull();
        run.RowsInserted.Should().Be(40);
        run.RowsSkipped.Should().Be(55);
        run.DistinctCrops.Should().Be(12);
        runs.AddCount.Should().Be(1, "one isolated commit for the Running row");
        runs.UpdateCount.Should().Be(1, "one isolated commit for the terminal transition");
    }

    [Fact]
    public async Task RunTracked_Success_StatusOnly_RecordsSucceededWithNullCounts()
    {
        var runs = new FakeIngestionRunRepository();

        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "WEATHER",
            _ => Task.FromResult<IngestionRunStats?>(null), CancellationToken.None);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Succeeded);
        run.RowsInserted.Should().BeNull("a status-only source leaves counts null, never fabricated zeros");
    }

    // Outcome mapping: a fail-safe source's Outcome drives the terminal status.

    [Fact]
    public async Task RunTracked_OutcomeFailed_RecordsFailed_WithSanitizedReason_AndDoesNotThrow()
    {
        var runs = new FakeIngestionRunRepository();
        var stats = new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: "ML returned 502.");

        // The body returned normally (fail-safe source) — the audit must still mark the row Failed.
        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "HARTI",
            _ => Task.FromResult<IngestionRunStats?>(stats), CancellationToken.None);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Failed, "a returned Outcome=Failed must not render as a green Succeeded row");
        run.ErrorSummary.Should().Be("ML returned 502.");
    }

    [Fact]
    public async Task RunTracked_OutcomeSkipped_RecordsSkipped_NotAFailureNorSuccess()
    {
        var runs = new FakeIngestionRunRepository();
        var stats = new IngestionRunStats(Outcome: IngestionRunOutcome.Skipped);

        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "HARTI",
            _ => Task.FromResult<IngestionRunStats?>(stats), CancellationToken.None);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Skipped, "a disabled/skipped source must be Skipped, not Succeeded");
        run.ErrorSummary.Should().BeNull();
    }

    // Lifecycle: the body throws -> Failed with a sanitized error.

    [Fact]
    public async Task RunTracked_BodyThrows_RecordsFailed_WithSanitizedErrorSummary_AndDoesNotThrow()
    {
        var runs = new FakeIngestionRunRepository();

        // Must NOT rethrow — the audit wrapper is the per-source fail-isolation belt.
        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "HARTI",
            _ => throw new InvalidOperationException("ML service unreachable"), CancellationToken.None);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Failed);
        run.ErrorSummary.Should().Be("InvalidOperationException: ML service unreachable");
        run.ErrorSummary.Should().NotContain(" at ", "no stack-frame marker may leak");
        run.ErrorSummary.Should().NotContain(".cs:");
        runs.UpdateCount.Should().Be(1);
    }

    // Audit-write failures never break the pass.

    [Fact]
    public async Task RunTracked_RunningInsertSaveFails_SourceStillRuns_AndPassDoesNotThrow()
    {
        var runs = new FakeIngestionRunRepository { ThrowOnSaveIndex = 1 }; // fail the Running commit
        var bodyRan = false;

        await IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "DAMBULLA_DEC",
            _ => { bodyRan = true; return Task.FromResult<IngestionRunStats?>(new IngestionRunStats()); },
            CancellationToken.None);

        bodyRan.Should().BeTrue("the source must run even when the audit Running-row write fails");
        runs.UpdateCount.Should().Be(0, "after the Running write fails the audit gives up on the row — no terminal write");
    }

    [Fact]
    public async Task RunTracked_TerminalSaveFails_DoesNotThrow_AndBodyOutcomeStands()
    {
        var runs = new FakeIngestionRunRepository { ThrowOnSaveIndex = 2 }; // fail the terminal commit
        var bodyRan = false;

        var act = () => IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "DAMBULLA_DEC",
            _ => { bodyRan = true; return Task.FromResult<IngestionRunStats?>(new IngestionRunStats()); },
            CancellationToken.None);

        await act.Should().NotThrowAsync("a failed terminal audit write must never break the pass");
        bodyRan.Should().BeTrue();
        // The in-memory transition still applied even though its persistence failed.
        runs.Single!.Status.Should().Be(IngestionRunStatus.Succeeded);
    }

    [Fact]
    public async Task RunTracked_BodyThrows_AndTerminalSaveFails_StillDoesNotThrow()
    {
        // Both belts stressed at once: the source failed AND recording that failure failed.
        var runs = new FakeIngestionRunRepository { ThrowOnSaveIndex = 2 };

        var act = () => IngestionRunAudit.RunTrackedAsync(runs, NullLogger.Instance, Guid.NewGuid(), "HARTI",
            _ => throw new InvalidOperationException("boom"), CancellationToken.None);

        await act.Should().NotThrowAsync();
        runs.Single!.Status.Should().Be(IngestionRunStatus.Failed);
    }

    // Context isolation: an audit write must not flush the ingestion scoped context.

    [Fact]
    public async Task AuditWrite_IsIsolatedFrom_TheIngestionScopedContext()
    {
        // Real IngestionRunRepository over an isolated-per-scope SQLite context. A single shared
        // in-memory SQLite connection kept open for the test's lifetime is the one database every
        // scope's context maps to (so persistence is observable across scopes), while each scope
        // still gets its OWN AgriForecastDbContext + ChangeTracker — exactly the production shape.
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AgriForecastDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        // Create ONLY the IngestionRuns table (the sole table the repo touches). EnsureCreated is
        // avoided because the full model's IngestionVerifications ISJSON check constraint is not a
        // SQLite function. Column affinities match EF's SQLite mapping (GUID/DateTime/DateOnly->TEXT,
        // enum/int->INTEGER), so IngestionRun round-trips through EF against this table.
        using (var createScope = scopeFactory.CreateScope())
        {
            await createScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
                .Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "IngestionRuns" (
                        "Id" TEXT NOT NULL CONSTRAINT "PK_IngestionRuns" PRIMARY KEY,
                        "BatchId" TEXT NOT NULL,
                        "Source" TEXT NOT NULL,
                        "StartedUtc" TEXT NOT NULL,
                        "FinishedUtc" TEXT NULL,
                        "Status" INTEGER NOT NULL,
                        "CoveredFromDate" TEXT NULL,
                        "CoveredToDate" TEXT NULL,
                        "RowsFetched" INTEGER NULL,
                        "RowsInserted" INTEGER NULL,
                        "RowsSkipped" INTEGER NULL,
                        "DistinctCrops" INTEGER NULL,
                        "ErrorSummary" TEXT NULL,
                        "CreatedAtUtc" TEXT NOT NULL
                    );
                    """);
        }

        var repo = new IngestionRunRepository(scopeFactory);

        // Simulate the ingestion pass's scoped context holding an UNSAVED (bogus/poisoned) tracked
        // entity. If the audit shared this context, its SaveChanges would flush this too.
        using (var ingestionScope = scopeFactory.CreateScope())
        {
            var ingestionCtx = ingestionScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
            var bogus = IngestionRun.StartRunning(Guid.NewGuid(), "BOGUS_UNSAVED", DateTime.UtcNow);
            ingestionCtx.IngestionRuns.Add(bogus); // tracked, NEVER saved

            // Run the full audit lifecycle for a real source via the repo (its own isolated scopes).
            await IngestionRunAudit.RunTrackedAsync(repo, NullLogger.Instance, Guid.NewGuid(), "WEATHER",
                _ => Task.FromResult<IngestionRunStats?>(null), CancellationToken.None);
        } // ingestion context disposed WITHOUT ever saving the bogus entity

        using var verifyScope = scopeFactory.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
        var persisted = verifyCtx.IngestionRuns.ToList();

        persisted.Should().ContainSingle(r => r.Source == "WEATHER")
            .Which.Status.Should().Be(IngestionRunStatus.Succeeded,
                "the audited source's run row is persisted through the audit's own context");
        persisted.Should().NotContain(r => r.Source == "BOGUS_UNSAVED",
            "the audit write uses its OWN context — the ingestion context's unsaved entity must never be flushed by it");
    }

    // Cancellation: the admin stop path.
    //
    // The bug this class of test exists for: the terminal write used to be handed the pass's own token.
    // After a stop that token is cancelled, the store throws before doing any I/O, the swallow-and-log
    // hides it, and the row is stranded at Running with FinishedUtc NULL. One stranded row then reads as
    // "ingestion is running" for the whole 120-minute staleness window, so Start answers 409
    // already_running and Stop answers 409 not_stoppable for two hours after a stop that looked like it
    // worked. The terminal write must therefore use CancellationToken.None.

    [Fact]
    public async Task CancelledSource_StillPersistsTheTerminalRow()
    {
        var runs = new FakeIngestionRunRepository();
        using var cts = new CancellationTokenSource();

        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "NEWS",
            _ =>
            {
                // The source is mid-flight when the admin presses stop.
                cts.Cancel();
                throw new TaskCanceledException("the pass was stopped");
            },
            cts.Token);

        runs.UpdateCount.Should().Be(1, "the terminal transition must be persisted even after a stop");
        runs.LastUpdateToken.CanBeCanceled.Should().BeFalse(
            "the terminal write must use CancellationToken.None, or it dies on the cancelled pass token");

        var run = runs.Single!;
        run.FinishedUtc.Should().NotBeNull("a stranded Running row reads as a live pass for two hours");
        run.Status.Should().Be(IngestionRunStatus.Failed);
    }

    // The reason is distinct so an operator is not sent hunting for an outage that never happened.
    // Failed, NOT Skipped: the status roll-up counts Skipped as part of "succeeded", so mapping a stopped
    // source to Skipped would make a halted pass report a green lastRunStatus.
    [Fact]
    public async Task CancelledSource_IsRecordedAsCancelled_NotAsAGenericFailure()
    {
        var runs = new FakeIngestionRunRepository();
        using var cts = new CancellationTokenSource();

        // Cancel from INSIDE the body, as a real stop does: the Running row is already committed by then.
        // (Pre-cancelling would fail the Running insert too, so no row would exist to assert on — itself a
        // safe outcome, but not the one under test.)
        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "HARTI",
            _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
            cts.Token);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Failed,
            "a cancelled source did not succeed, and Skipped would roll up as green");
        run.ErrorSummary.Should().Contain("Cancelled",
            "the run row must say it was stopped, not imply an outage");
        run.ErrorSummary.Should().Be(IngestionRunAudit.CancelledReason);
    }

    // A cancellation that is NOT ours (no stop requested) is still a genuine failure — most often an
    // HttpClient timeout, which surfaces as TaskCanceledException with an untripped token.
    [Fact]
    public async Task TimeoutStyleCancellation_WithNoStopRequested_StaysAGenericFailure()
    {
        var runs = new FakeIngestionRunRepository();

        await IngestionRunAudit.RunTrackedAsync(
            runs, NullLogger.Instance, Guid.NewGuid(), "CBSL",
            _ => throw new TaskCanceledException("HttpClient timeout"),
            CancellationToken.None);

        var run = runs.Single!;
        run.Status.Should().Be(IngestionRunStatus.Failed);
        run.ErrorSummary.Should().NotBe(IngestionRunAudit.CancelledReason,
            "no admin stop was requested, so this is a real timeout and must not be excused as a stop");
    }
}
