using AgriForecast.Application.Requests.Admin.Pipeline;
using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using AgriForecast.Application.Services;

namespace AgriForecast.API.Startup.Sentinel;

/// <summary>
/// What one nightly check did. Returned so the hosted service can log it and so the decision matrix
/// (state -> was an email sent, and which kind) is directly assertable in tests.
/// </summary>
public enum SentinelOutcome
{
    /// <summary>SMTP is not configured, so nothing was read and nothing was sent.</summary>
    Disabled,

    /// <summary>Health could not be read. No email — the sentinel never invents a verdict.</summary>
    ProbeFailed,

    /// <summary>Night was green and the all-clear heartbeat went out.</summary>
    HeartbeatSent,

    /// <summary>Night was green and Sentinel:SendGreenHeartbeat is off, so nothing was sent.</summary>
    HeartbeatSuppressed,

    /// <summary>Night was not green and the alert went out.</summary>
    AlertSent,

    /// <summary>An email was composed but the transport threw. Logged, swallowed.</summary>
    SendFailed
}

/// <summary>
/// What one macro-freshness check did. A SEPARATE enum from <see cref="SentinelOutcome"/> because it is a
/// separate question about a separate CronJob: the nightly verdict and the monthly macro signal share a
/// mailer and a timer, nothing else.
/// </summary>
public enum MacroAlertOutcome
{
    /// <summary>Macro data is inside its freshness window. Nothing to say.</summary>
    Fresh,

    /// <summary>Macro data is stale and the alert went out.</summary>
    AlertSent,

    /// <summary>Still the SAME stale episode, inside the repeat window, so no second email.</summary>
    AlertSuppressed,

    /// <summary>An email was composed but the transport threw. Logged, swallowed.</summary>
    SendFailed
}

/// <summary>
/// The nightly check itself: read pipeline health, wait out a night that has not settled yet, then send
/// exactly one email — an alert when something went wrong, an all-clear heartbeat when it did not.
/// <para>"Not settled" is two states, not one: a night still <c>running</c>, and a <c>missing</c> night
/// whose catch-up window has not closed (a node asleep at 21:00 may legitimately start at 02:00 and still
/// count). Both are re-read on the same cadence and the same bound.</para>
/// <para>Separated from the hosted service so the whole decision matrix runs in a unit test against a
/// fake probe, a fake mailer and a virtual clock. The hosted service is only the timer around it.</para>
/// <para>FAIL-OPEN by construction: every path except cancellation is caught, logged and turned into an
/// outcome. An alerting bug must never take down the API it is watching.</para>
/// <para>The same snapshot also carries a SECOND, unrelated signal — the monthly CBSL macro job's
/// freshness — which <see cref="RunMacroStalenessCheckAsync"/> turns into its OWN email with its OWN dedup
/// window. Its own email rather than a paragraph appended to the nightly one, for two reasons: with the
/// green heartbeat switched off a healthy night sends nothing at all, so an appended macro warning would
/// be swallowed exactly when the pipeline is otherwise fine; and a monthly problem filed under a subject
/// line naming last night sends the reader to the wrong pipeline.</para>
/// </summary>
public class PipelineSentinel
{
    private readonly IPipelineHealthProbe _probe;
    private readonly ISentinelMailer _mailer;
    private readonly ISentinelSettings _settings;
    private readonly IMacroFreshnessSettings _macroSettings;
    private readonly IPipelineScheduleSettings _schedule;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PipelineSentinel> _logger;

    // Dedup state for the macro alert: when the last one went out, or null when no episode is in progress.
    // Registered as a singleton, driven by ONE nightly loop, so a plain field is enough — there is no
    // second writer to race with.
    //
    // In PROCESS MEMORY on purpose. It is lost on a pod restart, which means a restart during a stale
    // episode sends one extra email. That is the deliberate direction: the alternative is a persisted
    // marker (a schema change on the read-only side of this endpoint) whose failure mode is a SUPPRESSED
    // alert, and under-alerting is the bug this whole feature exists to fix. One duplicate email per
    // restart is a cost worth paying for a rule with no way to fail quiet.
    private DateTime? _macroAlertSentUtc;

    public PipelineSentinel(
        IPipelineHealthProbe probe,
        ISentinelMailer mailer,
        ISentinelSettings settings,
        IMacroFreshnessSettings macroSettings,
        IPipelineScheduleSettings schedule,
        TimeProvider timeProvider,
        ILogger<PipelineSentinel> logger)
    {
        _probe = probe;
        _mailer = mailer;
        _settings = settings;
        _macroSettings = macroSettings;
        _schedule = schedule;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<SentinelOutcome> RunNightlyCheckAsync(CancellationToken cancellationToken)
    {
        if (!_mailer.IsConfigured)
        {
            // Belt and braces: the hosted service already refuses to start the loop without SMTP. This
            // guard means a direct caller cannot get past it either.
            return SentinelOutcome.Disabled;
        }

        var recheck = TimeSpan.FromMinutes(_settings.RunningRecheckMinutes);
        var startUtc = _timeProvider.GetUtcNow().UtcDateTime;

        // How long a night that has not settled may be waited out: up to ONE re-check interval before the
        // next scheduled fire. Past that the next pipeline run is about to start and would muddy the
        // picture, so the sentinel reports what it last saw rather than waiting forever. In practice this
        // is never reached — the health endpoint itself stops calling a stalled run "running" once
        // Ingestion:RunningStalenessMinutes lapses, which flips it to a terminal state first.
        var giveUpUtc = PipelineScheduleClock.ResolveNextOccurrenceUtc(
            startUtc, _schedule.ScheduleTimeZone, _schedule.LocalFireTime) - recheck;

        // When "nothing has run" stops being provisional and becomes a fact. The CronJob's
        // startingDeadlineSeconds (mirrored as CatchUpWindowMinutes, 6h) means a node that was asleep at
        // 21:00 may legitimately start the pipeline as late as 03:00 and still count as this night's run.
        // The 22:30 check sits INSIDE that window, so an empty window at 22:30 is not yet evidence of a
        // miss — emailing "did not run at all" there and having the banner read green the next morning
        // would burn the alert's credibility on the one state it exists to catch.
        var catchUpEndsUtc = PipelineScheduleClock
            .ResolveMostRecentFire(startUtc, _schedule.ScheduleTimeZone, _schedule.LocalFireTime)
            .Utc
            .AddMinutes(_schedule.CatchUpWindowMinutes);

        var health = await ProbeAsync(cancellationToken);
        if (health is null) return SentinelOutcome.ProbeFailed;

        var recheckCount = 0;
        while (IsProvisional(health.State, _timeProvider.GetUtcNow().UtcDateTime, catchUpEndsUtc)
               && _timeProvider.GetUtcNow().UtcDateTime + recheck <= giveUpUtc)
        {
            await Task.Delay(recheck, _timeProvider, cancellationToken);
            recheckCount++;

            var next = await ProbeAsync(cancellationToken);
            if (next is null)
            {
                // The night was in flight and now we cannot see it at all. Reporting the last known
                // "running" as if it were final would be a guess; staying quiet is the honest failure.
                return SentinelOutcome.ProbeFailed;
            }

            health = next;
        }

        if (recheckCount > 0)
        {
            _logger.LogInformation(
                "Pipeline sentinel re-checked an unsettled night {Count} time(s); final state {State}.",
                recheckCount, health.State);
        }

        // The macro signal, handled BEFORE the nightly verdict is composed and deliberately not after it.
        // Two reasons, both about not letting the daily path swallow it: the early return just below sends
        // nothing at all on a green night with the heartbeat off, and a daily send that throws returns
        // immediately. Placing it here means the only thing that can suppress a macro alert is the macro
        // dedup window itself. Everything below this line is the pre-existing daily path, untouched.
        var macroOutcome = await RunMacroStalenessCheckAsync(health, cancellationToken);
        if (macroOutcome != MacroAlertOutcome.Fresh)
        {
            _logger.LogInformation("Pipeline sentinel macro freshness check: {Outcome}.", macroOutcome);
        }

        var isGreen = health.State == PipelineHealthStates.Green;
        if (isGreen && !_settings.SendGreenHeartbeat)
        {
            _logger.LogInformation(
                "Pipeline sentinel: {Date} is green and the heartbeat is off; no email sent.",
                health.ExpectedForDate);
            return SentinelOutcome.HeartbeatSuppressed;
        }

        var zoneLabel = _schedule.ScheduleTimeZone.Id;
        var email = isGreen
            ? PipelineSentinelEmails.ComposeHeartbeat(
                health, _settings.AdminLogsUrl, _settings.LocalCheckTime, zoneLabel)
            : PipelineSentinelEmails.ComposeAlert(
                health, _settings.AdminLogsUrl, _settings.LocalCheckTime, zoneLabel);

        try
        {
            await _mailer.SendAsync(email, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A dead mailbox, a rotated app password, no network. Loud in the log, harmless to the API.
            // The exception is logged as-is: nothing in this path ever carries the SMTP password, which
            // the mailer keeps in a private field and never puts in a message.
            _logger.LogError(ex,
                "Pipeline sentinel could not send the {Kind} email for {Date} (state {State}).",
                isGreen ? "heartbeat" : "alert", health.ExpectedForDate, health.State);
            return SentinelOutcome.SendFailed;
        }

        _logger.LogInformation(
            "Pipeline sentinel sent the {Kind} email for {Date} (state {State}).",
            isGreen ? "heartbeat" : "alert", health.ExpectedForDate, health.State);

        return isGreen ? SentinelOutcome.HeartbeatSent : SentinelOutcome.AlertSent;
    }

    /// <summary>
    /// The macro half of the check: is the MONTHLY CBSL macro job still delivering data, and if not, has
    /// the owner been told recently enough?
    /// <para>Takes the snapshot the nightly check already read rather than probing again — one read a
    /// night answers both questions — but touches ONLY its macro* fields. The nightly <c>state</c> plays
    /// no part here and this method cannot change it: a green night with 43-day-old macro data is a real
    /// and reportable combination, and so is a failed night with perfectly fresh macro data.</para>
    /// <para>NEVER waits or re-checks. "Stale" is a fact about a 40-day window, not a race with a run that
    /// might still finish, so there is nothing to wait for — unlike a night that is still <c>running</c>.
    /// </para>
    /// <para>DEDUP: one email per stale EPISODE, then at most one every
    /// <c>MacroFreshness:AlertRepeatDays</c> (7 by default) for as long as that episode lasts. The episode
    /// marker is cleared the moment macro data reads fresh again, so a new outage alerts on the first
    /// night it is seen instead of inheriting the previous episode's silence. A send that FAILS does not
    /// set the marker, so a bounced alert is retried the next night rather than being deduped away.</para>
    /// <para>Public so the whole dedup matrix is assertable directly against a crafted snapshot; the
    /// nightly check calls it once per night on the way past.</para>
    /// </summary>
    public async Task<MacroAlertOutcome> RunMacroStalenessCheckAsync(
        PipelineHealth_GetDto health, CancellationToken cancellationToken)
    {
        if (!health.MacroStale)
        {
            if (_macroAlertSentUtc is not null)
            {
                _logger.LogInformation(
                    "Macro data is fresh again (age {AgeDays} day(s)); the macro-stale alert episode is closed.",
                    health.MacroDataAgeDays);
                _macroAlertSentUtc = null;
            }

            return MacroAlertOutcome.Fresh;
        }

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var repeat = TimeSpan.FromDays(_macroSettings.AlertRepeatDays);

        if (_macroAlertSentUtc is not null && nowUtc - _macroAlertSentUtc.Value < repeat)
        {
            _logger.LogInformation(
                "Macro data is still stale (age {AgeDays} day(s)) but the last alert went out at " +
                "{SentUtc:yyyy-MM-dd HH:mm:ss}Z, inside the {RepeatDays}-day repeat window; no email sent.",
                health.MacroDataAgeDays, _macroAlertSentUtc.Value, _macroSettings.AlertRepeatDays);
            return MacroAlertOutcome.AlertSuppressed;
        }

        var email = PipelineSentinelEmails.ComposeMacroStaleAlert(
            health,
            _macroSettings.StaleAfterDays,
            _macroSettings.AlertRepeatDays,
            _settings.AdminLogsUrl,
            _settings.LocalCheckTime,
            _schedule.ScheduleTimeZone.Id);

        try
        {
            await _mailer.SendAsync(email, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The marker is deliberately NOT set: an alert that never left the building must not start a
            // seven-day silence.
            _logger.LogError(ex,
                "Pipeline sentinel could not send the macro-stale alert (macro data age {AgeDays} day(s)).",
                health.MacroDataAgeDays);
            return MacroAlertOutcome.SendFailed;
        }

        _macroAlertSentUtc = nowUtc;
        _logger.LogWarning(
            "Pipeline sentinel sent the macro-stale alert: newest CBSL macro data is {AgeDays} day(s) old " +
            "(threshold {ThresholdDays}).",
            health.MacroDataAgeDays, _macroSettings.StaleAfterDays);

        return MacroAlertOutcome.AlertSent;
    }

    // Which states are not yet the night's final answer, and so are worth waiting on:
    //   * "running" — something is in flight, by definition unfinished;
    //   * "missing" — but ONLY while the catch-up window is still open, because until it closes an empty
    //     window means "not started YET", not "never started".
    // Everything else is terminal on sight. Note the asymmetry: a night that has already failed is never
    // re-checked in the hope it improves, because it will not.
    private static bool IsProvisional(string state, DateTime nowUtc, DateTime catchUpEndsUtc) =>
        state == PipelineHealthStates.Running ||
        (state == PipelineHealthStates.Missing && nowUtc < catchUpEndsUtc);

    // Null means "could not read". Never throws except on cancellation.
    private async Task<PipelineHealth_GetDto?> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _probe.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Usually the database being unreachable. Note what this means: the API cannot tell how the
            // night went, so it says nothing at all rather than emailing a state it did not observe. The
            // MISSING heartbeat is what covers this case — that is why the heartbeat defaults to on.
            _logger.LogError(ex, "Pipeline sentinel could not read pipeline health; no email sent.");
            return null;
        }
    }
}
