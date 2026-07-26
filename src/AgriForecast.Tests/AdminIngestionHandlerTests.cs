using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;
using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Services.IngestionRead;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Tests;

/// <summary>
/// PR 3 — unit tests for the admin ingestion read handlers (GetIngestionStatus / GetIngestionRuns)
/// and the IngestionStatusSettings config resolver. The DB is faked via a canned IIngestionReadStore
/// so state derivation, batch-outcome aggregation, the verification join (incl. null), paging math,
/// source filtering/canonicalization, and the serviceAddress fallback are exercised in isolation.
/// NOT covered here: the store's EF LINQ (verified by build + the existing live-DB conventions).
/// </summary>
public class AdminIngestionHandlerTests
{
    // ── Fake read store: canned data for status + real in-memory filter/page for runs ─────
    private sealed class FakeStore : IIngestionReadStore
    {
        public int RunCount;
        public DateTime? LatestUnfinishedStartedUtc;
        public IngestionRunHeadRow? LatestRun;
        public Dictionary<Guid, List<IngestionRunStatus>> BatchStatuses = new();
        public IngestionVerificationRow? LatestVerification;
        public List<IngestionWatermarkRow> Watermarks = new();

        public List<IngestionRunRow> AllRuns = new();
        public Dictionary<Guid, IngestionVerificationRow> VerificationsByBatch = new();

        public string? CapturedSource;
        public List<Guid>? CapturedBatchIds;

        // The source-exclusion set the handler asked for on the LAST status read (null = it asked for
        // none). Lets a test prove the handler actually passes the policy down, not just that the
        // numbers happen to work out.
        public IReadOnlyCollection<string>? CapturedExcluded;

        // RAW run rows. When this list is non-empty the four status reads are computed from it,
        // honestly applying excludeSources exactly as the SQL store does — that is what makes the
        // handler's state / lastRun derivation testable end-to-end. When it is EMPTY the canned
        // scalar fields above are returned instead, so the pre-existing tests are untouched.
        public List<FakeRun> Runs = new();

        public sealed record FakeRun(
            string Source, Guid BatchId, DateTime StartedUtc, DateTime? FinishedUtc,
            IngestionRunStatus Status, Guid Id);

        private bool UseRawRows => Runs.Count > 0;

        private IEnumerable<FakeRun> Visible(IReadOnlyCollection<string>? excludeSources)
        {
            CapturedExcluded = excludeSources;
            return excludeSources is { Count: > 0 }
                ? Runs.Where(r => !excludeSources.Contains(r.Source))
                : Runs;
        }

        public Task<int> GetRunCountAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => Task.FromResult(UseRawRows ? Visible(excludeSources).Count() : Capture(excludeSources, RunCount));

        public Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            if (!UseRawRows) return Task.FromResult(Capture(excludeSources, LatestUnfinishedStartedUtc));
            var unfinished = Visible(excludeSources).Where(r => r.FinishedUtc == null).ToList();
            return Task.FromResult(unfinished.Count == 0 ? null : (DateTime?)unfinished.Max(r => r.StartedUtc));
        }

        public Task<IngestionRunHeadRow?> GetLatestRunAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            if (!UseRawRows) return Task.FromResult(Capture(excludeSources, LatestRun));
            var latest = Visible(excludeSources)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.Id)
                .FirstOrDefault();
            return Task.FromResult(latest is null ? null : new IngestionRunHeadRow(latest.BatchId, latest.StartedUtc));
        }

        public Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
            Guid batchId, IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            if (!UseRawRows)
                return Task.FromResult<IReadOnlyList<IngestionRunStatus>>(
                    Capture(excludeSources, BatchStatuses.TryGetValue(batchId, out var s) ? s : new List<IngestionRunStatus>()));

            return Task.FromResult<IReadOnlyList<IngestionRunStatus>>(
                Visible(excludeSources).Where(r => r.BatchId == batchId).Select(r => r.Status).ToList());
        }

        // Records what the handler asked to exclude even on the canned-scalar path.
        private T Capture<T>(IReadOnlyCollection<string>? excludeSources, T value)
        {
            CapturedExcluded = excludeSources;
            return value;
        }

        public Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default)
            => Task.FromResult(LatestVerification);

        public Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<IngestionWatermarkRow>>(Watermarks);

        public Task<IngestionRunsPage> GetRunsPageAsync(
            int page, int pageSize, string? source, CancellationToken ct = default)
        {
            CapturedSource = source;
            var filtered = AllRuns
                .Where(r => source == null || r.Source == source)
                .OrderByDescending(r => r.StartedUtc).ThenByDescending(r => r.Id)
                .ToList();
            var total = filtered.Count;
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new IngestionRunsPage(items, total));
        }

        public Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
            IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default)
        {
            CapturedBatchIds = batchIds.ToList();
            IReadOnlyDictionary<Guid, IngestionVerificationRow> map = VerificationsByBatch
                .Where(kv => batchIds.Contains(kv.Key))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            return Task.FromResult(map);
        }
    }

    private sealed class FakeSettings : IIngestionStatusSettings
    {
        public string ServiceAddress { get; set; } = "unconfigured";
        public int RunningStalenessMinutes { get; set; } = 120;
    }

    private static GetIngestionStatusQueryHandler StatusHandler(FakeStore s, FakeSettings? cfg = null)
        => new(s, cfg ?? new FakeSettings());

    private static GetIngestionRunsQueryHandler RunsHandler(FakeStore s) => new(s);

    private static async Task<IngestionStatus_GetDto> Status(FakeStore s, FakeSettings? cfg = null)
        => (await StatusHandler(s, cfg).Handle(new GetIngestionStatusQuery(), default)).Data;

    private static IngestionRunRow Run(
        Guid batchId, string source, DateTime startedUtc, DateTime? finishedUtc = null,
        IngestionRunStatus status = IngestionRunStatus.Succeeded, Guid? id = null)
        => new(id ?? Guid.NewGuid(), batchId, source, startedUtc, finishedUtc, status,
            null, null, null, null, null, null, null);

    // ── STATE derivation ───────────────────────────────────────────────────────────
    [Fact]
    public async Task EmptyRunsTable_State_Unknown()
    {
        var dto = await Status(new FakeStore { RunCount = 0 });

        dto.State.Should().Be("unknown");
        dto.LastRunAtUtc.Should().BeNull();
        dto.LastRunStatus.Should().BeNull();
    }

    [Fact]
    public async Task FreshUnfinishedRun_State_Running()
    {
        var batch = Guid.NewGuid();
        var startedUtc = DateTime.UtcNow.AddMinutes(-5); // well inside the 120-min window
        var store = new FakeStore
        {
            RunCount = 1,
            LatestUnfinishedStartedUtc = startedUtc,
            LatestRun = new IngestionRunHeadRow(batch, startedUtc),
            BatchStatuses = { [batch] = new() { IngestionRunStatus.Running } }
        };

        var dto = await Status(store);

        dto.State.Should().Be("running");
        dto.LastRunAtUtc.Should().Be(startedUtc);
    }

    [Fact]
    public async Task CrashedStaleUnfinishedRun_State_Stopped()
    {
        var batch = Guid.NewGuid();
        var startedUtc = DateTime.UtcNow.AddMinutes(-200); // older than the 120-min window => crashed
        var store = new FakeStore
        {
            RunCount = 1,
            LatestUnfinishedStartedUtc = startedUtc,
            LatestRun = new IngestionRunHeadRow(batch, startedUtc),
            BatchStatuses = { [batch] = new() { IngestionRunStatus.Running } }
        };

        var dto = await Status(store);

        dto.State.Should().Be("stopped"); // never a fake "running"
    }

    [Fact]
    public async Task AllRunsFinished_NoUnfinished_State_Stopped()
    {
        var batch = Guid.NewGuid();
        var store = new FakeStore
        {
            RunCount = 3,
            LatestUnfinishedStartedUtc = null,
            LatestRun = new IngestionRunHeadRow(batch, DateTime.UtcNow.AddMinutes(-10)),
            BatchStatuses = { [batch] = new() { IngestionRunStatus.Succeeded } }
        };

        var dto = await Status(store);

        dto.State.Should().Be("stopped");
    }

    // ── lastRunStatus aggregation ───────────────────────────────────────────────────
    [Theory]
    [InlineData("succeeded", IngestionRunStatus.Succeeded, IngestionRunStatus.Skipped)]
    [InlineData("partial", IngestionRunStatus.Succeeded, IngestionRunStatus.Failed)]
    [InlineData("failed", IngestionRunStatus.Failed, IngestionRunStatus.Failed)]
    [InlineData("partial", IngestionRunStatus.Succeeded, IngestionRunStatus.Running)] // in-flight => partial
    public async Task LastRunStatus_AggregatesLatestBatch(string expected, params IngestionRunStatus[] statuses)
    {
        var batch = Guid.NewGuid();
        var store = new FakeStore
        {
            RunCount = statuses.Length,
            LatestUnfinishedStartedUtc = null,
            LatestRun = new IngestionRunHeadRow(batch, DateTime.UtcNow.AddMinutes(-10)),
            BatchStatuses = { [batch] = statuses.ToList() }
        };

        var dto = await Status(store);

        dto.LastRunStatus.Should().Be(expected);
    }

    // ── serviceAddress ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task ServiceAddress_IsEchoedFromSettings()
    {
        var dto = await Status(new FakeStore { RunCount = 0 },
            new FakeSettings { ServiceAddress = "prod: ingestion-worker-01" });

        dto.ServiceAddress.Should().Be("prod: ingestion-worker-01");
    }

    [Fact]
    public void ServiceAddress_FallsBackToUnconfigured_WhenConfigBlank()
    {
        var settings = new IngestionStatusSettings(new ConfigurationBuilder().Build());
        settings.ServiceAddress.Should().Be("unconfigured");
        settings.RunningStalenessMinutes.Should().Be(120);
    }

    [Fact]
    public void Settings_ReadServiceAddressAndStaleness_FromConfig()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:ServiceAddress"] = "label-x",
                ["Ingestion:RunningStalenessMinutes"] = "30"
            })
            .Build();

        var settings = new IngestionStatusSettings(config);

        settings.ServiceAddress.Should().Be("label-x");
        settings.RunningStalenessMinutes.Should().Be(30);
    }

    [Theory]
    [InlineData("abc")]  // non-numeric would throw with GetValue<int?>; must fall back, not 500
    [InlineData("0")]    // non-positive => default (a zero window makes every run look crashed)
    [InlineData("-5")]
    [InlineData("")]     // blank
    public void Settings_Staleness_FallsBackTo120_OnBadOrNonPositive(string value)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:RunningStalenessMinutes"] = value
            })
            .Build();

        var settings = new IngestionStatusSettings(config);

        settings.RunningStalenessMinutes.Should().Be(120);
    }

    // ── verification join (status) incl. null ───────────────────────────────────────
    [Fact]
    public async Task LastVerification_Null_WhenNoVerificationRows()
    {
        var dto = await Status(new FakeStore { RunCount = 0, LatestVerification = null });
        Assert.Null(dto.LastVerification);
    }

    [Fact]
    public async Task LastVerification_Mapped_WithTitleCaseStatus()
    {
        var store = new FakeStore
        {
            RunCount = 0,
            LatestVerification = new IngestionVerificationRow(
                Guid.NewGuid(), IngestionVerificationStatus.Warn,
                new DateTime(2026, 7, 20, 3, 0, 0, DateTimeKind.Utc),
                new DateOnly(2026, 7, 19), 8, 2, 0, "[]")
        };

        var dto = await Status(store);

        Assert.NotNull(dto.LastVerification);
        dto.LastVerification!.OverallStatus.Should().Be("Warn"); // title-case per contract
        dto.LastVerification.PipelineDate.Should().Be("2026-07-19");
        dto.LastVerification.NChecksPass.Should().Be(8);
        dto.LastVerification.NChecksWarn.Should().Be(2);
    }

    [Fact]
    public async Task Sources_MappedFromWatermarks_WithLowercaseStatus()
    {
        var store = new FakeStore
        {
            RunCount = 0,
            Watermarks =
            {
                new IngestionWatermarkRow("CBSL", IngestionSourceStatus.Disabled, null, null, "no parser yet"),
                new IngestionWatermarkRow("HARTI", IngestionSourceStatus.Ok,
                    new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 7, 19), "ok")
            }
        };

        var dto = await Status(store);

        dto.Sources.Should().HaveCount(2);
        dto.Sources.Single(s => s.Source == "CBSL").Status.Should().Be("disabled");
        var harti = dto.Sources.Single(s => s.Source == "HARTI");
        harti.Status.Should().Be("ok");
        harti.LastObservedDate.Should().Be("2026-07-19");
    }

    // ── RUNS: paging + source filter + verification join ────────────────────────────
    [Fact]
    public async Task Runs_PagingMath_ReturnsRequestedPageAndTotal()
    {
        var batch = Guid.NewGuid();
        var store = new FakeStore();
        // 5 runs, StartedUtc descending by index so ordering is deterministic.
        for (var i = 0; i < 5; i++)
            store.AllRuns.Add(Run(batch, "DAMBULLA_DEC", DateTime.UtcNow.AddMinutes(-i)));

        var result = await RunsHandler(store).Handle(
            new GetIngestionRunsQuery { Page = 2, PageSize = 2 }, default);
        var dto = result.Data;

        dto.Total.Should().Be(5);
        dto.Page.Should().Be(2);
        dto.PageSize.Should().Be(2);
        dto.Items.Should().HaveCount(2); // page 2 of size 2 over 5 rows
    }

    [Fact]
    public async Task Runs_SourceFilter_CanonicalizesAndFilters()
    {
        var batch = Guid.NewGuid();
        var store = new FakeStore();
        store.AllRuns.Add(Run(batch, "HARTI", DateTime.UtcNow.AddMinutes(-1)));
        store.AllRuns.Add(Run(batch, "DAMBULLA_DEC", DateTime.UtcNow.AddMinutes(-2)));

        // lowercase input must canonicalize to the stored "HARTI" before the store is queried.
        var result = await RunsHandler(store).Handle(
            new GetIngestionRunsQuery { Page = 1, PageSize = 20, Source = "harti" }, default);

        store.CapturedSource.Should().Be("HARTI");
        result.Data.Items.Should().OnlyContain(i => i.Source == "HARTI");
        result.Data.Total.Should().Be(1);
    }

    [Fact]
    public async Task Runs_BlankSource_PassesNullFilter()
    {
        var store = new FakeStore();
        store.AllRuns.Add(Run(Guid.NewGuid(), "NEWS", DateTime.UtcNow));

        await RunsHandler(store).Handle(new GetIngestionRunsQuery { Source = "  " }, default);

        store.CapturedSource.Should().BeNull();
    }

    [Fact]
    public async Task Runs_VerificationJoin_InclNull_AndChecksJsonPassthrough()
    {
        var batchWithVer = Guid.NewGuid();
        var batchNoVer = Guid.NewGuid();
        const string rawJson = "[{\"check\":\"row_count\",\"status\":\"pass\"}]";

        var store = new FakeStore();
        store.AllRuns.Add(Run(batchWithVer, "HARTI", DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow, IngestionRunStatus.Succeeded));
        store.AllRuns.Add(Run(batchNoVer, "WEATHER", DateTime.UtcNow.AddMinutes(-2),
            DateTime.UtcNow, IngestionRunStatus.Failed));
        store.VerificationsByBatch[batchWithVer] = new IngestionVerificationRow(
            batchWithVer, IngestionVerificationStatus.Pass,
            new DateTime(2026, 7, 20, 4, 0, 0, DateTimeKind.Utc),
            new DateOnly(2026, 7, 20), 10, 0, 0, rawJson);

        var dto = (await RunsHandler(store).Handle(new GetIngestionRunsQuery(), default)).Data;

        var withVer = dto.Items.Single(i => i.Source == "HARTI");
        Assert.NotNull(withVer.Verification);
        withVer.Verification!.OverallStatus.Should().Be("Pass");
        withVer.Verification.ChecksJson.Should().Be(rawJson); // verbatim passthrough
        withVer.Status.Should().Be("succeeded");

        var noVer = dto.Items.Single(i => i.Source == "WEATHER");
        Assert.Null(noVer.Verification);
        noVer.Status.Should().Be("failed");
    }

    // ── UTC Kind (so System.Text.Json emits the trailing Z) ─────────────────────────
    [Fact]
    public async Task Status_UtcFields_HaveUtcKind_SoJsonEmitsZ()
    {
        var batch = Guid.NewGuid();
        // EF materializes datetime2 as Unspecified — model that exactly.
        var unspecified = new DateTime(2026, 7, 20, 1, 0, 0, DateTimeKind.Unspecified);
        var store = new FakeStore
        {
            RunCount = 1,
            LatestUnfinishedStartedUtc = null,
            LatestRun = new IngestionRunHeadRow(batch, unspecified),
            BatchStatuses = { [batch] = new() { IngestionRunStatus.Succeeded } },
            LatestVerification = new IngestionVerificationRow(
                batch, IngestionVerificationStatus.Pass, unspecified,
                new DateOnly(2026, 7, 20), 1, 0, 0, "[]"),
            Watermarks = { new IngestionWatermarkRow("HARTI", IngestionSourceStatus.Ok, unspecified, null, null) }
        };

        var dto = await Status(store);

        dto.LastRunAtUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        dto.LastVerification!.RanAtUtc.Kind.Should().Be(DateTimeKind.Utc);
        dto.Sources.Single().LastSuccessUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Runs_UtcFields_HaveUtcKind_SoJsonEmitsZ()
    {
        var batch = Guid.NewGuid();
        var unspecified = new DateTime(2026, 7, 21, 2, 0, 0, DateTimeKind.Unspecified);
        var store = new FakeStore();
        store.AllRuns.Add(new IngestionRunRow(
            Guid.NewGuid(), batch, "HARTI", unspecified, unspecified,
            IngestionRunStatus.Succeeded, null, null, null, null, null, null, null));
        store.VerificationsByBatch[batch] = new IngestionVerificationRow(
            batch, IngestionVerificationStatus.Pass, unspecified,
            new DateOnly(2026, 7, 21), 1, 0, 0, "[]");

        var dto = (await RunsHandler(store).Handle(new GetIngestionRunsQuery(), default)).Data;

        var item = dto.Items.Single();
        item.StartedUtc.Kind.Should().Be(DateTimeKind.Utc);
        item.FinishedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        item.Verification!.RanAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }
}
