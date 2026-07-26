using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Services.PipelineHealth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Tests;

/// <summary>
/// GET /api/admin/pipeline/health — "did last night's pipeline run, and how did it end?".
/// <para>
/// Every state is derived by the REAL handler off an in-memory IngestionRuns/IngestionVerifications
/// table (the SharedRunTable pattern), with rows built through the real domain entities so a test can
/// never assert on a row shape the pipeline could not actually produce — a Failed row always carries a
/// FinishedUtc, an in-flight row never does. The schedule comes from the real PipelineScheduleSettings,
/// so the Asia/Colombo resolution and its fallbacks are under test too; only the clock is faked.
/// </para>
/// <para>
/// The incident this endpoint exists for: the 21:00 job silently stopped firing and nobody noticed for
/// eight mornings while CropFeatureDaily went stale and forecasts drifted ~17%. So the two rules with
/// the sharpest teeth here are "an empty window is 'missing', never a comfortable silence" and "clean
/// ingestion without a feature build is NOT green".
/// </para>
/// </summary>
public class PipelineHealthHandlerTests
{
    // 21:00 Asia/Colombo (UTC+05:30, no DST) on 2026-07-26 is 15:30Z; the 6h catch-up window closes at
    // 21:30Z the same UTC day.
    private static readonly DateTime FireUtc = new(2026, 7, 26, 15, 30, 0, DateTimeKind.Utc);
    private const string ExpectedNight = "2026-07-26";

    // The seven real ingestion sources, i.e. everything except FEATURE_BUILD.
    private static readonly string[] IngestionSourceKeys =
        IngestionSources.KnownKeys.Where(k => k != IngestionSources.FeatureBuild).ToArray();

    // In-memory IngestionRuns + IngestionVerifications, mirroring PipelineHealthReadStore's semantics —
    // including the two-pass batch scoping, which is the only reason a batch that merely SPILLED into
    // the window can be told apart from one that started in it.
    private sealed class FakeStore : IPipelineHealthReadStore
    {
        private readonly List<IngestionRun> _runs = new();
        private readonly List<IngestionVerification> _verifications = new();

        public FakeStore Add(params IngestionRun[] runs)
        {
            _runs.AddRange(runs);
            return this;
        }

        public FakeStore Add(IngestionVerification verification)
        {
            _verifications.Add(verification);
            return this;
        }

        public Task<IReadOnlyList<PipelineRunRow>> GetRunsForBatchesStartedBetweenAsync(
            DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var batchIds = _runs
                .Where(r => r.StartedUtc >= fromUtc && r.StartedUtc <= toUtc)
                .Select(r => r.BatchId)
                .ToHashSet();

            var rows = _runs
                .Where(r => batchIds.Contains(r.BatchId))
                .Select(r => new PipelineRunRow(r.BatchId, r.Source, r.StartedUtc, r.FinishedUtc, r.Status))
                .ToList();

            return Task.FromResult<IReadOnlyList<PipelineRunRow>>(rows);
        }

        public Task<IngestionVerificationRow?> GetVerificationForBatchOrDateAsync(
            Guid? batchId, DateOnly pipelineDate, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            var byBatch = batchId.HasValue
                ? Latest(_verifications.Where(v => v.BatchId == batchId.Value))
                : null;

            return Task.FromResult(byBatch
                ?? Latest(_verifications.Where(v => v.PipelineDate == pipelineDate)));
        }

        private static IngestionVerificationRow? Latest(IEnumerable<IngestionVerification> rows)
        {
            var row = rows.OrderByDescending(v => v.RunUtc).FirstOrDefault();
            return row is null
                ? null
                : new IngestionVerificationRow(
                    row.BatchId, row.OverallStatus, row.RunUtc, row.PipelineDate,
                    row.NChecksPass, row.NChecksWarn, row.NChecksFail, row.ChecksJson);
        }
    }

    private sealed class FixedClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }

    private sealed class FixedStaleness(int minutes) : IIngestionStatusSettings
    {
        public string ServiceAddress => "test-host";
        public int RunningStalenessMinutes => minutes;
    }

    private static async Task<PipelineHealth_GetDto> HealthAsync(
        FakeStore store, DateTime nowUtc, int stalenessMinutes = 120)
    {
        var schedule = new PipelineScheduleSettings(new ConfigurationBuilder().Build());
        var handler = new GetPipelineHealthQueryHandler(
            store, schedule, new FixedStaleness(stalenessMinutes), new FixedClock(nowUtc));

        var result = await handler.Handle(new GetPipelineHealthQuery(), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        return result.Data;
    }

    // --- row builders -------------------------------------------------------------------------

    private static IngestionRun Running(Guid batchId, string source, DateTime startedUtc) =>
        IngestionRun.StartRunning(batchId, source, startedUtc);

    private static IngestionRun Succeeded(Guid batchId, string source, DateTime startedUtc)
    {
        var run = IngestionRun.StartRunning(batchId, source, startedUtc);
        run.MarkSucceeded(startedUtc.AddMinutes(2), rowsInserted: 10);
        return run;
    }

    private static IngestionRun Skipped(Guid batchId, string source, DateTime startedUtc)
    {
        var run = IngestionRun.StartRunning(batchId, source, startedUtc);
        run.MarkSkipped(startedUtc.AddSeconds(1));
        return run;
    }

    private static IngestionRun Failed(Guid batchId, string source, DateTime startedUtc)
    {
        var run = IngestionRun.StartRunning(batchId, source, startedUtc);
        run.MarkFailed(startedUtc.AddMinutes(1), "transport failure");
        return run;
    }

    // A whole night's ingestion batch: every source succeeded, except that one is Skipped to keep the
    // "Skipped counts as good" half of the roll-up honest.
    private static IngestionRun[] CleanBatch(Guid batchId, DateTime firstStartedUtc) =>
        IngestionSourceKeys
            .Select((source, i) => source == IngestionSources.Cbsl
                ? Skipped(batchId, source, firstStartedUtc.AddMinutes(i))
                : Succeeded(batchId, source, firstStartedUtc.AddMinutes(i)))
            .ToArray();

    private static IngestionRun FeatureBuild(DateTime startedUtc, IngestionRunStatus status)
    {
        var batchId = Guid.NewGuid(); // the Python writer always mints a fresh solo batch id
        var run = IngestionRun.StartRunning(batchId, IngestionSources.FeatureBuild, startedUtc);
        switch (status)
        {
            case IngestionRunStatus.Succeeded:
                run.MarkSucceeded(startedUtc.AddMinutes(4), rowsInserted: 5000);
                break;
            case IngestionRunStatus.Failed:
                run.MarkFailed(startedUtc.AddMinutes(1), "build_features blew up");
                break;
            case IngestionRunStatus.Running:
                break; // left unfinished on purpose
            default:
                run.MarkSkipped(startedUtc.AddSeconds(1));
                break;
        }
        return run;
    }

    private static IngestionVerification Verification(
        Guid? batchId, IngestionVerificationStatus status, DateTime runUtc, DateOnly? pipelineDate = null) =>
        IngestionVerification.Create(
            batchId,
            pipelineDate ?? DateOnly.FromDateTime(runUtc),
            runUtc,
            status,
            nChecksPass: status == IngestionVerificationStatus.Pass ? 14 : 12,
            nChecksWarn: status == IngestionVerificationStatus.Warn ? 2 : 0,
            nChecksFail: status == IngestionVerificationStatus.Fail ? 2 : 0,
            checksJson: "[]",
            createdAtUtc: runUtc);

    // --- the happy night ----------------------------------------------------------------------

    [Fact]
    public async Task Green_WhenIngestionVerificationAndTheFeatureBuildAllLanded()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.Green);
        health.ExpectedForDate.Should().Be(ExpectedNight);
        health.BatchId.Should().Be(batchId);
        health.StartedUtc.Should().Be(FireUtc.AddMinutes(1));
        health.StartedUtc!.Value.Kind.Should().Be(DateTimeKind.Utc, "the FE parses these as instants");
        health.VerificationStatus.Should().Be("Pass");
        health.FeatureBuildStatus.Should().Be("succeeded");
        health.CheckedAtUtc.Should().Be(FireUtc.AddHours(2));
    }

    // A Warn verification does not stop the pipeline, so it must not colour the banner either — it is
    // reported alongside a green night, not instead of it.
    [Fact]
    public async Task Green_WhenVerificationOnlyWarned()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Warn, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.Green);
        health.VerificationStatus.Should().Be("Warn");
    }

    // --- the incident: nothing ran ------------------------------------------------------------

    [Fact]
    public async Task Missing_WhenNothingRanInTheWindow()
    {
        // Last night ran fine; tonight the CronJob is suspended. The previous night's rows must not be
        // mistaken for tonight's — that is exactly how the failure went unnoticed for eight mornings.
        var lastNight = FireUtc.AddDays(-1);
        var store = new FakeStore()
            .Add(CleanBatch(Guid.NewGuid(), lastNight.AddMinutes(1)))
            .Add(FeatureBuild(lastNight.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(null, IngestionVerificationStatus.Pass, lastNight.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(5));

        health.State.Should().Be(PipelineHealthStates.Missing);
        health.ExpectedForDate.Should().Be(ExpectedNight);
        health.BatchId.Should().BeNull();
        health.StartedUtc.Should().BeNull();
        health.VerificationStatus.Should().BeNull("last night's verification says nothing about tonight");
        health.FeatureBuildStatus.Should().BeNull();
    }

    // The pinned edge: between the fire time and the first committed run row there is no seventh
    // "starting up" state. The endpoint says "missing" with a null startedUtc and the banner resolves it
    // by polling. Over-alerting for a few minutes beats under-reporting a suspended schedule.
    [Fact]
    public async Task Missing_InTheMinutesBetweenTheFireTimeAndTheFirstRunRow()
    {
        var health = await HealthAsync(new FakeStore(), FireUtc.AddMinutes(2));

        health.State.Should().Be(PipelineHealthStates.Missing);
        health.StartedUtc.Should().BeNull();
        health.BatchId.Should().BeNull();
        health.ExpectedForDate.Should().Be(ExpectedNight);
    }

    // A batch that began BEFORE the fire time and was still writing rows after it belongs to the
    // previous window, not this one. Scoping on "has a row in the window" alone would adopt it and
    // report a night that never ran as healthy.
    [Fact]
    public async Task Missing_WhenTheOnlyBatchInTheWindowStartedBeforeIt()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore().Add(
            Succeeded(batchId, IngestionSources.DambullaDec, FireUtc.AddHours(-1)),
            Succeeded(batchId, IngestionSources.Weather, FireUtc.AddMinutes(10)));

        var health = await HealthAsync(store, FireUtc.AddHours(3));

        health.State.Should().Be(PipelineHealthStates.Missing);
        health.BatchId.Should().BeNull();
    }

    // --- in flight ----------------------------------------------------------------------------

    [Fact]
    public async Task Running_WhileASourceIsStillInFlight()
    {
        var batchId = Guid.NewGuid();
        var now = FireUtc.AddMinutes(12);
        var store = new FakeStore().Add(
            Succeeded(batchId, IngestionSources.DambullaDec, FireUtc.AddMinutes(1)),
            Running(batchId, IngestionSources.Weather, FireUtc.AddMinutes(10)));

        var health = await HealthAsync(store, now);

        health.State.Should().Be(PipelineHealthStates.Running);
        health.BatchId.Should().Be(batchId);
        health.StartedUtc.Should().Be(FireUtc.AddMinutes(1));
    }

    // Ingestion is done and verification passed, but the feature build — the point of the whole run —
    // is still going. Reporting green here would flip the banner to "all good" mid-run.
    [Fact]
    public async Task Running_WhileTheFeatureBuildIsStillInFlight()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Running))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddMinutes(30));

        health.State.Should().Be(PipelineHealthStates.Running);
        health.FeatureBuildStatus.Should().Be("running");
    }

    // An unfinished row past the staleness window is a crashed process, not a running one. The batch
    // still holds a Running row, so the roll-up is "partial" — honest about the sources that did land.
    [Fact]
    public async Task StaleUnfinishedRow_IsNotRunning()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore().Add(
            Succeeded(batchId, IngestionSources.DambullaDec, FireUtc.AddMinutes(1)),
            Running(batchId, IngestionSources.Weather, FireUtc.AddMinutes(10)));

        var health = await HealthAsync(store, FireUtc.AddHours(4), stalenessMinutes: 120);

        health.State.Should().NotBe(PipelineHealthStates.Running);
        health.State.Should().Be(PipelineHealthStates.Partial);
    }

    // --- the gate -----------------------------------------------------------------------------

    [Fact]
    public async Task GateBlocked_WhenVerificationFailed()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(Verification(batchId, IngestionVerificationStatus.Fail, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.GateBlocked);
        health.VerificationStatus.Should().Be("Fail");
        health.FeatureBuildStatus.Should().BeNull("the gate stops the pod before build_features runs");
    }

    // The adjudicated-resume shape: an admin looked at the failed checks, decided the data was usable
    // and re-ran the feature build by hand. That build writes its OWN batch id, so it cannot retro-fit
    // the night into green — the gate did fail, and the banner shows both facts.
    [Fact]
    public async Task GateBlocked_EvenAfterAHandRunFeatureBuildSucceeds()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddHours(1), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Fail, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.GateBlocked);
        health.VerificationStatus.Should().Be("Fail");
        health.FeatureBuildStatus.Should().Be("succeeded");
    }

    // The verify step writes a BatchId when the CronJob threads one through, but an ad-hoc run does not.
    // Falling back to PipelineDate keeps the gate visible either way.
    [Fact]
    public async Task GateBlocked_WhenTheFailingVerificationCarriesNoBatchId()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(null, IngestionVerificationStatus.Fail, FireUtc.AddMinutes(20),
                pipelineDate: new DateOnly(2026, 7, 26)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.GateBlocked);
    }

    // --- ingestion outcomes -------------------------------------------------------------------

    [Fact]
    public async Task Partial_WhenSomeSourcesFailed()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(Succeeded(batchId, IngestionSources.DambullaDec, FireUtc.AddMinutes(1)),
                 Failed(batchId, IngestionSources.Harti, FireUtc.AddMinutes(3)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.Partial,
            "a clean feature build does not erase a source that failed to land");
    }

    [Fact]
    public async Task Failed_WhenEverySourceFailed()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore().Add(
            Failed(batchId, IngestionSources.DambullaDec, FireUtc.AddMinutes(1)),
            Failed(batchId, IngestionSources.Weather, FireUtc.AddMinutes(3)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.Failed);
    }

    // --- wire vocabulary ----------------------------------------------------------------------

    // featureBuildStatus is rendered through the SAME mapper as the ingestion runs log, because the FE
    // banner feeds it straight into its existing run-status label map. A different spelling here — a
    // title-case "Succeeded", a "success", an enum int — does not fail loudly, it quietly degrades the
    // label to "Unknown". So the exact strings are pinned per status, including the skipped case the
    // Python writer does not emit today.
    [Theory]
    [InlineData(IngestionRunStatus.Succeeded, "succeeded")]
    [InlineData(IngestionRunStatus.Failed, "failed")]
    [InlineData(IngestionRunStatus.Running, "running")]
    [InlineData(IngestionRunStatus.Skipped, "skipped")]
    public async Task FeatureBuildStatus_UsesTheExactIngestionRunStatusWireWords(
        IngestionRunStatus status, string expected)
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), status));

        // Far enough past the fire that a Running row is stale, so this asserts the rendered word rather
        // than tripping over the in-flight short circuit.
        var health = await HealthAsync(store, FireUtc.AddHours(5));

        health.FeatureBuildStatus.Should().Be(expected);
    }

    // verificationStatus keeps the title-case spellings exactly as IngestionVerifications persists them
    // and as the admin ingestion card already emits — one vocabulary across both screens.
    [Theory]
    [InlineData(IngestionVerificationStatus.Pass, "Pass")]
    [InlineData(IngestionVerificationStatus.Warn, "Warn")]
    [InlineData(IngestionVerificationStatus.Fail, "Fail")]
    public async Task VerificationStatus_UsesTheExactFrozenSpellings(
        IngestionVerificationStatus status, string expected)
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, status, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.VerificationStatus.Should().Be(expected);
    }

    // The banner switches on these six and nothing else; a seventh value would render as an unstyled
    // blank rather than an error.
    [Fact]
    public void StateVocabulary_IsExactlyTheSixPinnedValues()
    {
        var states = typeof(PipelineHealthStates)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => (string)f.GetValue(null)!)
            .ToArray();

        states.Should().BeEquivalentTo(new[]
        {
            "green", "running", "partial", "failed", "gate_blocked", "missing"
        });
    }

    // --- THE false-green guard ----------------------------------------------------------------

    // The exact silent failure: every source green, verification green, and build_features never ran.
    // CropFeatureDaily goes stale and the model quietly serves older features. This must not be green.
    [Fact]
    public async Task NotGreen_WhenTheFeatureBuildRowIsAbsent()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(4));

        health.State.Should().Be(PipelineHealthStates.Failed);
        health.FeatureBuildStatus.Should().BeNull();
    }

    [Fact]
    public async Task Failed_WhenTheFeatureBuildFailed()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Failed))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(2));

        health.State.Should().Be(PipelineHealthStates.Failed);
        health.FeatureBuildStatus.Should().Be("failed");
    }

    // A stale unfinished FEATURE_BUILD row is a crashed build, so the night is not green either — and
    // the row's own status is still reported verbatim so the banner can say what happened.
    [Fact]
    public async Task Failed_WhenTheFeatureBuildHungAndWentStale()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Running))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(5), stalenessMinutes: 120);

        health.State.Should().Be(PipelineHealthStates.Failed);
        health.FeatureBuildStatus.Should().Be("running");
    }

    // A hand-run rebuild after a failed automatic one: the newest attempt is the current truth.
    [Fact]
    public async Task Green_WhenAReRunFeatureBuildSucceedsAfterAFailedOne()
    {
        var batchId = Guid.NewGuid();
        var store = new FakeStore()
            .Add(CleanBatch(batchId, FireUtc.AddMinutes(1)))
            .Add(FeatureBuild(FireUtc.AddMinutes(25), IngestionRunStatus.Failed))
            .Add(FeatureBuild(FireUtc.AddHours(2), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, FireUtc.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(3));

        health.State.Should().Be(PipelineHealthStates.Green);
        health.FeatureBuildStatus.Should().Be("succeeded");
    }

    // --- the clock ----------------------------------------------------------------------------

    // Which night is "last night" is a Colombo question, and the machine watching is in the UK. At 22:00
    // UK in winter it is already tomorrow in Colombo, and at 01:30 UK in summer the UTC date has rolled
    // over too — in both cases the fire time being reported on is still the previous evening's. Deriving
    // the date from UtcNow.Date or from "today in Colombo" gets these wrong by a full day.
    [Theory]
    // 22:00 UK (GMT) on the 15th = 03:30 Colombo on the 16th -> still the 15th's fire.
    [InlineData("2026-01-15T22:00:00Z", "2026-01-15")]
    // 01:30 UK (BST) on the 16th = 06:00 Colombo on the 16th -> still the 15th's fire.
    [InlineData("2026-06-16T00:30:00Z", "2026-06-15")]
    // One minute BEFORE tonight's 21:00 Colombo fire -> the night being reported on is yesterday's.
    [InlineData("2026-07-26T15:29:00Z", "2026-07-25")]
    // One minute after it -> tonight.
    [InlineData("2026-07-26T15:31:00Z", "2026-07-26")]
    // Deep inside the catch-up window, after the Colombo date has rolled over.
    [InlineData("2026-07-26T20:00:00Z", "2026-07-26")]
    public async Task ExpectedForDate_NamesTheColomboFireTime_NotTheUtcOrUkDate(
        string nowIso, string expectedForDate)
    {
        var now = DateTime.Parse(nowIso, null, System.Globalization.DateTimeStyles.AdjustToUniversal |
                                             System.Globalization.DateTimeStyles.AssumeUniversal);

        var health = await HealthAsync(new FakeStore(), now);

        health.ExpectedForDate.Should().Be(expectedForDate);
    }

    // The machine was asleep at 21:00 and woke at 01:30 Colombo. k8s still starts the job inside
    // startingDeadlineSeconds, so this is a late run of THAT night, not a miss and not tomorrow's run.
    [Fact]
    public async Task LateStartInsideTheCatchUpWindow_StillCountsAsThatNight()
    {
        var batchId = Guid.NewGuid();
        var lateStart = FireUtc.AddHours(4.5);
        var store = new FakeStore()
            .Add(CleanBatch(batchId, lateStart))
            .Add(FeatureBuild(lateStart.AddMinutes(25), IngestionRunStatus.Succeeded))
            .Add(Verification(batchId, IngestionVerificationStatus.Pass, lateStart.AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(5.5));

        health.State.Should().Be(PipelineHealthStates.Green);
        health.ExpectedForDate.Should().Be(ExpectedNight);
        health.StartedUtc.Should().Be(lateStart);
    }

    // Past the catch-up window the same rows are no longer this night's run. The window is not a
    // formality — it is what stops a run that started at 04:00 from papering over a missed 21:00.
    [Fact]
    public async Task StartAfterTheCatchUpWindow_DoesNotCountAsThatNight()
    {
        var batchId = Guid.NewGuid();
        var tooLate = FireUtc.AddHours(7);
        var store = new FakeStore()
            .Add(CleanBatch(batchId, tooLate))
            .Add(FeatureBuild(tooLate.AddMinutes(25), IngestionRunStatus.Succeeded));

        var health = await HealthAsync(store, FireUtc.AddHours(8));

        health.State.Should().Be(PipelineHealthStates.Missing);
        health.ExpectedForDate.Should().Be(ExpectedNight);
    }

    // Two batches in one window (an admin pressed Run now after the scheduled pass) — the latest wins.
    [Fact]
    public async Task LatestBatchInTheWindowWins()
    {
        var early = Guid.NewGuid();
        var late = Guid.NewGuid();
        var store = new FakeStore()
            .Add(Failed(early, IngestionSources.DambullaDec, FireUtc.AddMinutes(1)))
            .Add(CleanBatch(late, FireUtc.AddHours(1)))
            .Add(FeatureBuild(FireUtc.AddHours(2), IngestionRunStatus.Succeeded))
            .Add(Verification(late, IngestionVerificationStatus.Pass, FireUtc.AddHours(1).AddMinutes(20)));

        var health = await HealthAsync(store, FireUtc.AddHours(3));

        health.BatchId.Should().Be(late);
        health.State.Should().Be(PipelineHealthStates.Green);
    }
}

/// <summary>
/// The schedule config seam. These values mirror k8s/pipeline-daily.yaml, and the defaults have to hold
/// on their own because no deployment sets the section today.
/// </summary>
public class PipelineScheduleSettingsTests
{
    private static PipelineScheduleSettings Build(params (string Key, string? Value)[] values) =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build());

    [Fact]
    public void Defaults_MirrorTheCronJobManifest()
    {
        var settings = Build();

        settings.LocalFireTime.Should().Be(new TimeOnly(21, 0), "schedule: 0 21 * * *");
        settings.CatchUpWindowMinutes.Should().Be(360, "startingDeadlineSeconds: 21600");
        settings.ScheduleTimeZone.GetUtcOffset(new DateTime(2026, 7, 26, 15, 30, 0, DateTimeKind.Utc))
            .Should().Be(TimeSpan.FromMinutes(330), "Asia/Colombo is a fixed UTC+05:30");
    }

    [Fact]
    public void Overrides_AreHonoured()
    {
        var settings = Build(
            ("PipelineSchedule:LocalFireTime", "06:15"),
            ("PipelineSchedule:CatchUpWindowMinutes", "90"));

        settings.LocalFireTime.Should().Be(new TimeOnly(6, 15));
        settings.CatchUpWindowMinutes.Should().Be(90);
    }

    // A typo in configuration must not throw at construction: that would be a 500 on the one endpoint
    // whose job is to report that something is wrong.
    [Theory]
    [InlineData("not-a-time", "not-a-number", "Not/A_Zone")]
    [InlineData("", "0", "")]
    [InlineData("25:99", "-5", "Mars/Olympus")]
    public void MalformedValues_FallBackInsteadOfThrowing(string fireTime, string window, string zone)
    {
        var settings = Build(
            ("PipelineSchedule:LocalFireTime", fireTime),
            ("PipelineSchedule:CatchUpWindowMinutes", window),
            ("PipelineSchedule:TimeZone", zone));

        settings.LocalFireTime.Should().Be(new TimeOnly(21, 0));
        settings.CatchUpWindowMinutes.Should().Be(360);
        settings.ScheduleTimeZone.GetUtcOffset(DateTime.UtcNow).Should().Be(TimeSpan.FromMinutes(330),
            "an unresolvable zone falls back to a fixed +05:30 rather than failing the endpoint");
    }

    // The Windows spelling of the same zone must resolve to the same offset, so a Windows host does not
    // silently shift the whole window by 5.5 hours.
    [Fact]
    public void WindowsTimeZoneId_ResolvesToTheSameOffset()
    {
        var settings = Build(("PipelineSchedule:TimeZone", "Sri Lanka Standard Time"));

        settings.ScheduleTimeZone.GetUtcOffset(new DateTime(2026, 7, 26, 15, 30, 0, DateTimeKind.Utc))
            .Should().Be(TimeSpan.FromMinutes(330));
    }
}
