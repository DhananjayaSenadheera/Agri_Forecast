namespace AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;

// Response for GET /api/admin/pipeline/health — "did last night's pipeline run, and how did it end?".
// Every status field is a pre-rendered string so the wire shape is frozen and never an enum int.
// The shape is pinned with the FE banner; adding or renaming a field is a two-sided change.
public class PipelineHealth_GetDto
{
    // yyyy-MM-dd — the Asia/Colombo date of the most recent 21:00 fire time that has already passed.
    // This is the NIGHT being reported on, not "today": at 02:00 Colombo (or 20:30 UK the evening
    // before) it still names the previous calendar date, because that is the fire time that has passed.
    public string ExpectedForDate { get; set; } = string.Empty;

    // One of PipelineHealthStates. See GetPipelineHealthQueryHandler for the derivation order.
    //
    // EDGE — "missing" right after the fire time: state flips to "missing" the moment the 21:00 fire
    // passes and stays there until the pipeline's first run row commits (pod scheduling, image pulls).
    // There is deliberately no seventh "not started yet" state: a grace period of roughly 15 minutes is
    // normal, and the banner is expected to resolve it by polling rather than by the API guessing.
    // Over-alerting for a few minutes is the safe direction here — under-reporting a suspended CronJob
    // is the incident this endpoint exists to catch. When state is "missing", startedUtc and batchId are
    // always null.
    public string State { get; set; } = string.Empty;

    // The batch this night's verdict was read off, or null when nothing ran in the window.
    public Guid? BatchId { get; set; }

    // The first run row of that batch, i.e. when the night's pipeline actually started. Null when
    // nothing ran. May be hours after the fire time: a machine asleep at 21:00 that wakes inside the
    // 6h catch-up window still counts as this night's run.
    public DateTime? StartedUtc { get; set; }

    // "Pass" | "Warn" | "Fail", or null when no verification ran for this night. "Warn" does not block
    // the pipeline, so it never changes state on its own — it is surfaced for the banner to show.
    public string? VerificationStatus { get; set; }

    // "succeeded" | "failed" | "running" | "skipped", or null when no FEATURE_BUILD row exists in the
    // window. Reported separately from state because the feature build writes its OWN BatchId (it runs
    // standalone after verification) and can therefore be re-run by hand after an adjudicated gate
    // failure. In that case state stays "gate_blocked" and this reads "succeeded" — both facts are true
    // and the banner shows both.
    public string? FeatureBuildStatus { get; set; }

    // When this snapshot was taken, so the banner can show staleness of its own poll.
    public DateTime CheckedAtUtc { get; set; }
}
