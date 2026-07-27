using AgriForecast.Application.Requests.Admin.Pipeline;
using AgriForecast.Application.Services;

namespace AgriForecast.API.Startup.Sentinel;

/// <summary>
/// The timer around <see cref="PipelineSentinel"/>: once a night, at Sentinel:LocalCheckTime in the
/// pipeline's own time zone (default 22:30 Asia/Colombo — 90 minutes after the 21:00 fire), wake up and
/// run one check.
/// <para>Why this exists: the 21:00 job once stopped firing and nobody noticed for eight mornings while
/// the feature store went stale and forecasts drifted ~17%. The health endpoint made that visible to
/// anyone who opened the admin page; this makes it visible to an owner who does not.</para>
/// <para>Every iteration is wrapped: a failure logs and the loop continues at the next check time, with
/// a short cool-down so a persistent fault cannot become a hot loop. When SMTP is not configured the
/// service logs ONE line and exits its loop — the API must run perfectly well with no email set up.</para>
/// </summary>
public class PipelineSentinelHostedService : BackgroundService
{
    // Cool-down after an unexpected loop failure, so a fault that trips before the wait cannot spin.
    private static readonly TimeSpan FaultCooldown = TimeSpan.FromMinutes(5);

    private readonly PipelineSentinel _sentinel;
    private readonly ISentinelMailer _mailer;
    private readonly ISentinelSettings _settings;
    private readonly IPipelineScheduleSettings _schedule;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PipelineSentinelHostedService> _logger;

    public PipelineSentinelHostedService(
        PipelineSentinel sentinel,
        ISentinelMailer mailer,
        ISentinelSettings settings,
        IPipelineScheduleSettings schedule,
        TimeProvider timeProvider,
        ILogger<PipelineSentinelHostedService> logger)
    {
        _sentinel = sentinel;
        _mailer = mailer;
        _settings = settings;
        _schedule = schedule;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_mailer.IsConfigured)
        {
            // ONE clear line, then idle. Naming the keys (never the values) is what turns "why am I not
            // getting mail?" into a one-minute fix.
            _logger.LogWarning(
                "Pipeline sentinel disabled: Smtp not configured. Set Smtp:Host, Smtp:User, Smtp:To and " +
                "Smtp:Password (Smtp__Password comes from the agri-smtp secret in k8s; run " +
                "k8s/create-secrets.sh). The API runs normally, but nobody will be emailed when a night " +
                "goes wrong.");
            return;
        }

        _logger.LogInformation(
            "Pipeline sentinel armed: nightly check at {CheckTime} {Zone}; green heartbeat {Heartbeat}.",
            _settings.LocalCheckTime.ToString("HH\\:mm"),
            _schedule.ScheduleTimeZone.Id,
            _settings.SendGreenHeartbeat ? "ON" : "OFF");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var nextCheckUtc = PipelineScheduleClock.ResolveNextOccurrenceUtc(
                    nowUtc, _schedule.ScheduleTimeZone, _settings.LocalCheckTime);

                // Strictly-after by construction, so this delay is always positive and the loop can never
                // spin — including right after a check that finished at the check time itself.
                await Task.Delay(nextCheckUtc - nowUtc, _timeProvider, stoppingToken);

                var outcome = await _sentinel.RunNightlyCheckAsync(stoppingToken);
                _logger.LogInformation("Pipeline sentinel nightly check: {Outcome}.", outcome);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pipeline sentinel loop iteration failed; continuing.");
                try
                {
                    await Task.Delay(FaultCooldown, _timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }
}
