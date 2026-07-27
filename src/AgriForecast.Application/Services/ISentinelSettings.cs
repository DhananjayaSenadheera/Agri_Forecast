namespace AgriForecast.Application.Services;

/// <summary>
/// Config seam for the nightly pipeline email sentinel ("Sentinel" section). Resolved in Infrastructure
/// so nothing above it takes a Microsoft.Extensions.Configuration dependency, exactly like
/// IPipelineScheduleSettings and IIngestionStatusSettings.
/// </summary>
public interface ISentinelSettings
{
    /// <summary>
    /// Wall-clock time in the PIPELINE's time zone (IPipelineScheduleSettings.ScheduleTimeZone) at which
    /// the sentinel reads the health endpoint. Default 22:30 — 90 minutes after the 21:00 fire, which is
    /// past the health endpoint's transient "running" windows on a normal night, so the first read is
    /// usually already the final verdict.
    /// </summary>
    TimeOnly LocalCheckTime { get; }

    /// <summary>
    /// Whether a clean night still sends a short all-green heartbeat. Default TRUE, and that is the
    /// point: an alert-only sentinel is indistinguishable from a broken sentinel, so the nightly
    /// heartbeat is what makes silence suspicious. The owner can turn it off
    /// (Sentinel:SendGreenHeartbeat=false) once the noise outweighs the reassurance.
    /// </summary>
    bool SendGreenHeartbeat { get; }

    /// <summary>
    /// How long to wait before re-reading a night that is still "running". Default 30 minutes. The
    /// sentinel keeps re-checking until the night reaches a terminal state or until one interval before
    /// the NEXT scheduled fire, whichever comes first.
    /// </summary>
    int RunningRecheckMinutes { get; }

    /// <summary>
    /// Deep link put at the bottom of every message so the owner can go straight from the mail to the
    /// evidence. Defaults to the cluster UI's ingestion log
    /// (http://localhost:30080/admin/logs/ingestion); override per deployment.
    /// </summary>
    string AdminLogsUrl { get; }
}
