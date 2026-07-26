namespace AgriForecast.Application.Services;

// Config seam describing the daily pipeline's SCHEDULE, so the health handler can work out which fire
// time it should be reporting on. These values mirror k8s/pipeline-daily.yaml — schedule "0 21 * * *",
// timeZone "Asia/Colombo", startingDeadlineSeconds 21600 — and must be changed together with it.
// The Infrastructure implementation resolves the time zone (including the id fallbacks) so the
// Application layer never touches configuration or a zone lookup that can throw.
public interface IPipelineScheduleSettings
{
    // The zone the CronJob's schedule is expressed in. Already resolved, never null.
    TimeZoneInfo ScheduleTimeZone { get; }

    // Wall-clock time in ScheduleTimeZone at which the pipeline fires (21:00).
    TimeOnly LocalFireTime { get; }

    // How long after the fire time a late start still counts as "this night's run" — the .NET mirror of
    // startingDeadlineSeconds. A machine asleep at 21:00 that wakes within this window runs the pipeline
    // and must not be reported as a miss.
    int CatchUpWindowMinutes { get; }
}
