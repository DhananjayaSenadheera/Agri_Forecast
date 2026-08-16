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
    //
    // EDGE — "running" with a finished batch and no feature build: the pipeline's steps run in separate
    // containers, and between the last ingestion row and the first FEATURE_BUILD row sit the mirror,
    // verify, news and sentiment steps. For those minutes the batch looks complete with no build in
    // sight, which is shape-identical to a build that never ran. While ingestion finished recently the
    // endpoint reports "running" rather than "failed", so a healthy night does not flash red every
    // evening; once that recency window lapses the same shape becomes "failed" as it should.
    public string State { get; set; } = string.Empty;

    // The batch this night's verdict was read off, or null when nothing ran in the window.
    public Guid? BatchId { get; set; }

    // The first run row of that batch, i.e. when the night's pipeline actually started. Null when
    // nothing ran. May be hours after the fire time: a machine asleep at 21:00 that wakes inside the
    // 6h catch-up window still counts as this night's run.
    public DateTime? StartedUtc { get; set; }

    // "Pass" | "Warn" | "Fail" exactly as IngestionVerifications persists them, or null when no
    // verification ran for this night. "Warn" does not block
    // the pipeline, so it never changes state on its own — it is surfaced for the banner to show.
    public string? VerificationStatus { get; set; }

    // "succeeded" | "failed" | "running" | "skipped" — the same lowercase words the ingestion runs log
    // uses — or null when no FEATURE_BUILD row exists for this night. Reported separately from state
    // because the feature build writes its OWN BatchId (it runs standalone after verification) and can
    // therefore be re-run by hand hours later, after an adjudicated gate failure. In that case state
    // stays "gate_blocked" and this reads "succeeded" — both facts are true and the banner shows both.
    // Scoped to the whole night (fire time to the next fire time), NOT to the 6h catch-up window, which
    // bounds only when the run may START.
    public string? FeatureBuildStatus { get; set; }

    // When this snapshot was taken, so the banner can show staleness of its own poll.
    public DateTime CheckedAtUtc { get; set; }

    // --- the MONTHLY macro job, reported ALONGSIDE the nightly verdict -----------------------------
    //
    // Everything above describes ONE NIGHT of the daily pipeline. The three fields below describe a
    // different CronJob on a different cadence (k8s/pipeline-monthly.yaml, the 1st of each month) and are
    // deliberately NOT folded into State: the six PipelineHealthStates values are the daily state machine
    // and the FE banner maps them one-for-one, so a seventh value — or a nightly state quietly flipped to
    // "failed" because a monthly job died two weeks ago — would make the banner lie about last night and
    // would break every consumer of the existing ladder. Two independent signals, reported as two
    // independent facts.
    //
    // The incident: the monthly macro job was OOMKilled on 2026-08-01 and nobody knew for 15 days, because
    // both the banner and the email sentinel only ever looked at the daily pipeline.

    // True when the newest MacroSeriesPoints row was retrieved more than MacroFreshness:StaleAfterDays
    // ago (40 by default, STRICT greater-than), and ALSO true when the table is empty. Never null: the
    // question always has an answer, and "we hold no macro data at all" is the worst answer, not an
    // absent one.
    public bool MacroStale { get; set; }

    // Whole days between the newest macro retrieval and CheckedAtUtc, floored. NULL only when the table
    // is empty — there is no age to report and zero would read as "refreshed today". A consumer must
    // therefore treat (macroStale == true && macroDataAgeDays == null) as "no macro data at all", not as
    // a missing field.
    public int? MacroDataAgeDays { get; set; }

    // The newest MacroSeriesPoints.RetrievedAtUtc itself, or null on an empty table, so the banner and
    // the alert email can name the date rather than only the age. A monthly pass refreshes this even when
    // it inserts no new rows, so it tracks "when macro data last landed", which is the thing at risk.
    public DateTime? MacroLastRetrievedUtc { get; set; }
}
