namespace AgriForecast.Application.Services;

/// <summary>
/// Config seam for the MONTHLY CBSL macro job's freshness signal ("MacroFreshness" section). A separate
/// seam from <see cref="IPipelineScheduleSettings"/> on purpose: that one mirrors k8s/pipeline-daily.yaml
/// and answers "which NIGHT am I reporting on", while these two knobs describe a different CronJob
/// (k8s/pipeline-monthly.yaml, "0 21 1 * *") that has no night, no batch and no catch-up window. Folding
/// them into the daily schedule seam would invite exactly the overloading this feature avoids.
/// </summary>
public interface IMacroFreshnessSettings
{
    /// <summary>
    /// How old the newest MacroSeriesPoints.RetrievedAtUtc may be before macro data is called stale.
    /// Default 40 days, compared with a STRICT greater-than (exactly 40 days old is still fresh), the
    /// same operator the ML staleness caps use.
    /// <para>Why 40. The job fires on the 1st of each month, so on a healthy schedule the newest data is
    /// at most one cycle old — 31 days across the longest months, and around 37 once a late start inside
    /// the 6h catch-up window and a run that has to wait for a bulletin are allowed for. 40 sits above
    /// that worst normal case with a few days of headroom, and well below two cycles (59-62 days), so ONE
    /// missed monthly run is caught roughly nine days after the miss instead of at the next month's run.
    /// Loosening it past ~55 would let a whole missed cycle hide, which is the 15-day blind spot this
    /// exists to close.</para>
    /// </summary>
    int StaleAfterDays { get; }

    /// <summary>
    /// How often the sentinel may re-send the macro-stale alert while the SAME stale episode continues.
    /// Default 7 days. Without it the nightly loop would email every single night for as long as the
    /// macro job stays broken, which is how an operator learns to filter the alert into a folder.
    /// <para>The episode resets the moment macro data reads fresh again, so a new outage alerts on the
    /// first night it is detected rather than waiting out the previous episode's window.</para>
    /// </summary>
    int AlertRepeatDays { get; }
}
