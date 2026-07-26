using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;

// Answers "did last night's pipeline run, and how did it end?" for the admin banner.
//
// The night is derived, never asked for: take the most recent 21:00 Asia/Colombo fire time that has
// passed, and look at the batches that started inside [fireTime, fireTime + CatchUpWindowMinutes] — the
// .NET mirror of the CronJob's startingDeadlineSeconds, so a machine that was asleep at 21:00 and woke
// at 01:00 still reports as this night's run rather than a miss. The LATEST qualifying batch wins.
//
// State, in strict priority order:
//   1. no ingestion batch in the window                       -> "missing"   (catches a suspended CronJob)
//   2. a run row is unfinished and fresher than the staleness -> "running"
//   3. verification for this night is Fail                    -> "gate_blocked"
//   4. batch roll-up "failed" / "partial"                     -> "failed" / "partial"
//   5. otherwise green — UNLESS the feature build is missing or did not succeed, which is "failed"
//
// Rule 5 is the point of the whole endpoint. Ingestion landing cleanly while build_features never ran
// leaves CropFeatureDaily stale and forecasts quietly drifting, which is exactly the silent failure this
// banner exists to surface; calling that night green would reproduce the bug in the alerting.
//
// Staleness deliberately reuses the ingestion card's Ingestion:RunningStalenessMinutes knob, so
// "running" means the same thing on both screens.
public class GetPipelineHealthQueryHandler
    : IRequestHandler<GetPipelineHealthQuery, Result<PipelineHealth_GetDto>>
{
    private readonly IPipelineHealthReadStore _store;
    private readonly IPipelineScheduleSettings _schedule;
    private readonly IIngestionStatusSettings _ingestionSettings;
    private readonly TimeProvider _timeProvider;

    public GetPipelineHealthQueryHandler(
        IPipelineHealthReadStore store,
        IPipelineScheduleSettings schedule,
        IIngestionStatusSettings ingestionSettings,
        TimeProvider timeProvider)
    {
        _store = store;
        _schedule = schedule;
        _ingestionSettings = ingestionSettings;
        _timeProvider = timeProvider;
    }

    public async Task<Result<PipelineHealth_GetDto>> Handle(
        GetPipelineHealthQuery request, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var fire = ResolveMostRecentFire(nowUtc, _schedule.ScheduleTimeZone, _schedule.LocalFireTime);

        var windowStart = fire.Utc;
        var windowEnd = fire.Utc.AddMinutes(_schedule.CatchUpWindowMinutes);

        var rows = await _store.GetRunsForBatchesStartedBetweenAsync(windowStart, windowEnd, cancellationToken);

        // FEATURE_BUILD is not an ingestion source and carries its own solo BatchId, so it can never be
        // the batch we pick — but it IS the signal rule 5 turns on, so it is read separately.
        var excluded = new HashSet<string>(
            IngestionSources.ExcludedFromServiceState, StringComparer.OrdinalIgnoreCase);

        var batch = rows
            .Where(r => !excluded.Contains(r.Source))
            .GroupBy(r => r.BatchId)
            .Select(g => new BatchWindow(g.Key, g.Min(r => r.StartedUtc), g.ToList()))
            .Where(b => b.FirstStartedUtc >= windowStart && b.FirstStartedUtc <= windowEnd)
            .OrderByDescending(b => b.FirstStartedUtc)
            .ThenByDescending(b => b.BatchId)
            .FirstOrDefault();

        // Every feature-build attempt inside the window, newest first. More than one means a hand-run
        // rebuild after an adjudicated gate failure; the newest is the current truth.
        var featureBuildRows = rows
            .Where(r => excluded.Contains(r.Source)
                        && r.StartedUtc >= windowStart && r.StartedUtc <= windowEnd)
            .OrderByDescending(r => r.StartedUtc)
            .ToList();

        var featureBuildStatus = featureBuildRows.Count == 0
            ? null
            : IngestionStatusStrings.ToWire(featureBuildRows[0].Status);

        var verification = await _store.GetVerificationForBatchOrDateAsync(
            batch?.BatchId, fire.LocalDate, cancellationToken);

        var state = DeriveState(batch, featureBuildRows, featureBuildStatus, verification, nowUtc);

        var dto = new PipelineHealth_GetDto
        {
            ExpectedForDate = fire.LocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            State = state,
            BatchId = batch?.BatchId,
            StartedUtc = batch is null ? null : AsUtc(batch.FirstStartedUtc),
            VerificationStatus = verification is null
                ? null
                : IngestionStatusStrings.ToWire(verification.OverallStatus),
            FeatureBuildStatus = featureBuildStatus,
            CheckedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)
        };

        return Result<PipelineHealth_GetDto>.Success(dto);
    }

    private string DeriveState(
        BatchWindow? batch,
        IReadOnlyList<PipelineRunRow> featureBuildRows,
        string? featureBuildStatus,
        IngestionVerificationRow? verification,
        DateTime nowUtc)
    {
        if (batch is null)
            return PipelineHealthStates.Missing;

        // An unfinished row only means "running" while it is fresh. Past the staleness window it is a
        // crashed process, and reporting a hung pipeline as running is the same lie as a fake green.
        // The feature-build rows count here too: unlike the ingestion status card, the feature build is
        // part of THIS pipeline, so a build still in flight keeps the whole night "running".
        var freshThreshold = nowUtc.AddMinutes(-_ingestionSettings.RunningStalenessMinutes);
        var anyFreshUnfinished = batch.Rows.Concat(featureBuildRows)
            .Any(r => r.FinishedUtc is null && r.StartedUtc >= freshThreshold);
        if (anyFreshUnfinished)
            return PipelineHealthStates.Running;

        // The gate wins over the batch roll-up and over a later hand-run feature build: ingestion can
        // look perfectly clean and the data still be untrustworthy, and if a human rebuilt features
        // after adjudicating the failure the honest answer is still "the gate blocked this night".
        if (verification?.OverallStatus == IngestionVerificationStatus.Fail)
            return PipelineHealthStates.GateBlocked;

        var rollup = IngestionBatchRollup.Aggregate(batch.Rows.Select(r => r.Status).ToList());
        if (rollup == IngestionBatchRollup.Failed) return PipelineHealthStates.Failed;
        if (rollup == IngestionBatchRollup.Partial) return PipelineHealthStates.Partial;

        // Ingestion is clean and the gate did not fail, so the only remaining question is whether the
        // feature store was actually rebuilt. Anything other than a succeeded build — missing, failed,
        // a stale still-"running" row, or a skip — leaves the model serving yesterday's features, so it
        // is reported as failed with featureBuildStatus carrying the detail.
        return featureBuildStatus == IngestionStatusStrings.ToWire(IngestionRunStatus.Succeeded)
            ? PipelineHealthStates.Green
            : PipelineHealthStates.Failed;
    }

    // The most recent scheduled fire time that has already passed, as both a UTC instant and the local
    // (Asia/Colombo) date that names the night. Computed from the zone rather than from UTC arithmetic:
    // 21:00 Colombo is 15:30 UTC, so between 18:30 UTC and midnight UTC the Colombo date has already
    // rolled over while the fire being reported on is still yesterday's.
    private static (DateTime Utc, DateOnly LocalDate) ResolveMostRecentFire(
        DateTime nowUtc, TimeZoneInfo tz, TimeOnly localFireTime)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        var localDate = DateOnly.FromDateTime(nowLocal);
        if (localDate.ToDateTime(localFireTime) > nowLocal)
            localDate = localDate.AddDays(-1);

        return (ToUtcInstant(localDate.ToDateTime(localFireTime), tz), localDate);
    }

    // Wall clock -> UTC, without throwing on a DST transition. Sri Lanka has no DST so neither branch
    // fires today, but the zone is configurable and a health endpoint that 500s on a clock change would
    // be worse than useless.
    private static DateTime ToUtcInstant(DateTime localWallClock, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        // Skipped by a spring-forward: no such instant exists, so take the offset in effect just before
        // the transition, which lands on the first real instant at or after the intended one.
        if (tz.IsInvalidTime(unspecified))
            return unspecified - tz.GetUtcOffset(unspecified);

        // Repeated by a fall-back: take the larger offset, i.e. the FIRST of the two occurrences, so the
        // window opens at the earlier fire rather than an hour late.
        if (tz.IsAmbiguousTime(unspecified))
            return unspecified - tz.GetAmbiguousTimeOffsets(unspecified).Max();

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }

    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // One night's batch: its id, when its first source row started, and every row it owns.
    private sealed record BatchWindow(Guid BatchId, DateTime FirstStartedUtc, IReadOnlyList<PipelineRunRow> Rows);
}
