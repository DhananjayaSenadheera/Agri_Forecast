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
/// The nightly check itself: read pipeline health, wait out a night that has not settled yet, then send
/// exactly one email — an alert when something went wrong, an all-clear heartbeat when it did not.
/// <para>"Not settled" is two states, not one: a night still <c>running</c>, and a <c>missing</c> night
/// whose catch-up window has not closed (a node asleep at 21:00 may legitimately start at 02:00 and still
/// count). Both are re-read on the same cadence and the same bound.</para>
/// <para>Separated from the hosted service so the whole decision matrix runs in a unit test against a
/// fake probe, a fake mailer and a virtual clock. The hosted service is only the timer around it.</para>
/// <para>FAIL-OPEN by construction: every path except cancellation is caught, logged and turned into an
/// outcome. An alerting bug must never take down the API it is watching.</para>
/// </summary>
public class PipelineSentinel
{
    private readonly IPipelineHealthProbe _probe;
    private readonly ISentinelMailer _mailer;
    private readonly ISentinelSettings _settings;
    private readonly IPipelineScheduleSettings _schedule;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PipelineSentinel> _logger;

    public PipelineSentinel(
        IPipelineHealthProbe probe,
        ISentinelMailer mailer,
        ISentinelSettings settings,
        IPipelineScheduleSettings schedule,
        TimeProvider timeProvider,
        ILogger<PipelineSentinel> logger)
    {
        _probe = probe;
        _mailer = mailer;
        _settings = settings;
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
