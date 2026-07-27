using System.Globalization;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.PipelineSentinel;

// Resolves the "Sentinel" configuration section, applying the documented fallbacks. Same shape as
// IngestionStatusSettings / PipelineScheduleSettings: read the RAW string and TryParse, never
// GetValue<T>, so a typo in a config value can never throw at construction — that would take the whole
// API down over an alerting knob.
public class SentinelSettings : ISentinelSettings
{
    private const string CheckTimeKey = "Sentinel:LocalCheckTime";
    private const string HeartbeatKey = "Sentinel:SendGreenHeartbeat";
    private const string RecheckKey = "Sentinel:RunningRecheckMinutes";
    private const string LogsUrlKey = "Sentinel:AdminLogsUrl";

    // 90 minutes after the 21:00 fire: past the ingestion -> mirror -> verify -> news -> feature-build
    // chain and past the health endpoint's transient "running" windows on a healthy night.
    private static readonly TimeOnly DefaultCheckTime = new(22, 30);
    private const int DefaultRecheckMinutes = 30;
    private const string DefaultLogsUrl = "http://localhost:30080/admin/logs/ingestion";

    public TimeOnly LocalCheckTime { get; }
    public bool SendGreenHeartbeat { get; }
    public int RunningRecheckMinutes { get; }
    public string AdminLogsUrl { get; }

    public SentinelSettings(IConfiguration configuration)
    {
        LocalCheckTime = TimeOnly.TryParseExact(
            configuration[CheckTimeKey], "HH:mm", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var checkTime)
            ? checkTime
            : DefaultCheckTime;

        // Defaults to TRUE on an absent or unparseable value. Failing open towards MORE mail is the safe
        // direction: the heartbeat is what tells the owner the sentinel itself is alive.
        SendGreenHeartbeat = !bool.TryParse(configuration[HeartbeatKey], out var heartbeat) || heartbeat;

        RunningRecheckMinutes = int.TryParse(
            configuration[RecheckKey], NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var recheck) && recheck > 0
            ? recheck
            : DefaultRecheckMinutes;

        var logsUrl = configuration[LogsUrlKey];
        AdminLogsUrl = string.IsNullOrWhiteSpace(logsUrl) ? DefaultLogsUrl : logsUrl.Trim();
    }
}
