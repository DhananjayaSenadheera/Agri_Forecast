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
//   5. clean batch, no feature build YET, ingestion only just
//      finished (the gap between the two containers)          -> "running"
//   6. otherwise green — UNLESS the feature build is missing or did not succeed, which is "failed"
//
// Rule 6 is the point of the whole endpoint. Ingestion landing cleanly while build_features never ran
// leaves CropFeatureDaily stale and forecasts quietly drifting, which is exactly the silent failure this
// banner exists to surface; calling that night green would reproduce the bug in the alerting.
//
// Staleness deliberately reuses the ingestion card's Ingestion:RunningStalenessMinutes knob, so
// "running" means the same thing on both screens.
//
// SECOND, INDEPENDENT SIGNAL — macro freshness. The monthly CBSL macro job is a different CronJob on a
// different cadence, and it failed silently for 15 days because nothing here watched it. It is reported
// through the macro* fields ONLY; it never touches `state`. The six-value ladder above is the daily state
// machine, pinned with the FE banner, and overloading it would both break that contract and make the
// banner lie about last night over a fortnight-old monthly failure.
public class GetPipelineHealthQueryHandler
    : IRequestHandler<GetPipelineHealthQuery, Result<PipelineHealth_GetDto>>
{
    private readonly IPipelineHealthReadStore _store;
    private readonly IPipelineScheduleSettings _schedule;
    private readonly IIngestionStatusSettings _ingestionSettings;
    private readonly IMacroFreshnessSettings _macroSettings;
    private readonly TimeProvider _timeProvider;

    public GetPipelineHealthQueryHandler(
        IPipelineHealthReadStore store,
        IPipelineScheduleSettings schedule,
        IIngestionStatusSettings ingestionSettings,
        IMacroFreshnessSettings macroSettings,
        TimeProvider timeProvider)
    {
        _store = store;
        _schedule = schedule;
        _ingestionSettings = ingestionSettings;
        _macroSettings = macroSettings;
        _timeProvider = timeProvider;
    }

    public async Task<Result<PipelineHealth_GetDto>> Handle(
        GetPipelineHealthQuery request, CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var tz = _schedule.ScheduleTimeZone;
        var fire = PipelineScheduleClock.ResolveMostRecentFire(nowUtc, tz, _schedule.LocalFireTime);

        // Two windows, deliberately different sizes.
        // The BATCH must have STARTED inside the catch-up window, because that is the k8s rule for what
        // counts as this night's scheduled run.
        // The FEATURE BUILD is scoped to the whole night, up to the next fire time: it is the last step,
        // so a late catch-up run can legitimately reach it after the 6h deadline has passed, and a
        // hand-run rebuild after an adjudicated gate failure can land hours later still. Scoping it to
        // the catch-up window instead made both invisible — a real miss, caught against live data where
        // a 22:25Z manual build read as "no feature build" on a 15:32Z run.
        var windowStart = fire.Utc;
        var windowEnd = fire.Utc.AddMinutes(_schedule.CatchUpWindowMinutes);
        var nightEnd = PipelineScheduleClock.ResolveNextFire(fire, tz, _schedule.LocalFireTime);

        var rows = await _store.GetRunsForBatchesStartedBetweenAsync(windowStart, nightEnd, cancellationToken);

        // Every source in ExcludedFromServiceState (today FEATURE_BUILD and FORECAST_SNAPSHOT) carries its
        // own solo BatchId, so none of them can ever be the ingestion-Worker batch we pick here.
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

        // Every feature-build attempt this night, newest first. More than one means a rebuild after a
        // failed or adjudicated first attempt; the newest is the current truth.
        //
        // Filtered on Source == FeatureBuild SPECIFICALLY, not "any excluded source": FORECAST_SNAPSHOT is
        // also excluded from `batch` above (it must never win batch selection — PR 0c reviewer B2), but it
        // runs even later than FEATURE_BUILD every night and is report-only (farmer-portfolio PRD §3.7 —
        // the snapshot pass must never gate ingest/verify/train). If this filter reused the whole
        // `excluded` set, the newest excluded row on a healthy night would be the FORECAST_SNAPSHOT row,
        // not the FEATURE_BUILD row, and its outcome would silently become featureBuildStatus below —
        // a Failed snapshot flipping this banner (and the sentinel email) red over a report-only pass, or
        // a Succeeded snapshot papering over a feature build that actually failed. FORECAST_SNAPSHOT must
        // play NO role anywhere in this handler beyond being excluded from `batch`.
        var featureBuildRows = rows
            .Where(r => string.Equals(r.Source, IngestionSources.FeatureBuild, StringComparison.OrdinalIgnoreCase)
                        && r.StartedUtc >= windowStart && r.StartedUtc < nightEnd)
            .OrderByDescending(r => r.StartedUtc)
            .ToList();

        var featureBuildStatus = featureBuildRows.Count == 0
            ? null
            : IngestionStatusStrings.ToWire(featureBuildRows[0].Status);

        var verification = await _store.GetVerificationForBatchOrDateAsync(
            batch?.BatchId, fire.LocalDate, windowStart, cancellationToken);

        var state = DeriveState(batch, featureBuildRows, featureBuildStatus, verification, nowUtc);

        // Read AFTER the state is derived, and never passed into DeriveState — the compiler cannot enforce
        // "the macro signal must not reach the daily ladder", so the separation is kept structural here.
        var macroRetrievedAtUtc = await _store.GetLatestMacroRetrievedAtUtcAsync(cancellationToken);
        var macro = EvaluateMacroFreshness(macroRetrievedAtUtc, nowUtc, _macroSettings.StaleAfterDays);

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
            CheckedAtUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            MacroStale = macro.IsStale,
            MacroDataAgeDays = macro.AgeDays,
            MacroLastRetrievedUtc = macroRetrievedAtUtc is null ? null : AsUtc(macroRetrievedAtUtc.Value)
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

        // THE GAP. Ingestion is clean and finished, but the pipeline's later steps live in other
        // containers: mirror, verify, news and sentiment all run between the last ingestion row and the
        // first FEATURE_BUILD row. For those minutes every batch row has a FinishedUtc and no feature
        // build exists yet, which looks identical to "the build never ran" — and on a perfectly healthy
        // night that would fire a red alert every single evening, training the operator to ignore it.
        // While ingestion finished RECENTLY the honest answer is that the night is still in progress.
        // Past the staleness window the same shape really is a build that never started, and falls
        // through to the rule below.
        if (featureBuildStatus is null)
        {
            var lastRowFinishedUtc = batch.Rows.Max(r => r.FinishedUtc ?? r.StartedUtc);
            if (lastRowFinishedUtc >= nowUtc.AddMinutes(-_ingestionSettings.RunningStalenessMinutes))
                return PipelineHealthStates.Running;
        }

        // Ingestion is clean and the gate did not fail, so the only remaining question is whether the
        // feature store was actually rebuilt. Anything other than a succeeded build — missing, failed,
        // a stale still-"running" row, or a skip — leaves the model serving yesterday's features, so it
        // is reported as failed with featureBuildStatus carrying the detail.
        return featureBuildStatus == IngestionStatusStrings.ToWire(IngestionRunStatus.Succeeded)
            ? PipelineHealthStates.Green
            : PipelineHealthStates.Failed;
    }

    // Is the newest macro data older than the configured window, and by how much?
    //
    // The comparison is on the raw TimeSpan with a STRICT greater-than — exactly 40.0 days old is still
    // fresh, 40 days plus a tick is stale — matching the operator the ML staleness caps already use, so
    // "older than N days" means one thing across the system. The reported age is the FLOOR of the same
    // span, so a 40-day-and-2-hour gap reads as 40 days AND as stale; rounding the age up to 41 to make
    // the two agree would be inventing a day that has not passed.
    //
    // Two edges, both decided towards over-reporting rather than a comfortable silence:
    //   * empty table -> stale with a NULL age. There is no age, and 0 would read as "refreshed today",
    //     which is the exact false-green this feature exists to prevent.
    //   * a RetrievedAtUtc in the FUTURE (clock skew between the pod that wrote it and this one) -> not
    //     stale, age clamped to 0. A negative age is not a fact about the data, and refusing to alert on
    //     data that is too NEW is the safe direction.
    private static (bool IsStale, int? AgeDays) EvaluateMacroFreshness(
        DateTime? latestRetrievedAtUtc, DateTime nowUtc, int staleAfterDays)
    {
        if (latestRetrievedAtUtc is null)
            return (true, null);

        var age = nowUtc - latestRetrievedAtUtc.Value;
        if (age < TimeSpan.Zero)
            return (false, 0);

        return (age > TimeSpan.FromDays(staleAfterDays), (int)Math.Floor(age.TotalDays));
    }

    // The fire-time / next-fire / DST-safe conversion maths moved to PipelineScheduleClock when the email
    // sentinel started needing the same answers. Shared deliberately: the banner and the alert email must
    // report on the SAME night, and two copies of this arithmetic would eventually disagree.

    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // One night's batch: its id, when its first source row started, and every row it owns.
    private sealed record BatchWindow(Guid BatchId, DateTime FirstStartedUtc, IReadOnlyList<PipelineRunRow> Rows);
}
