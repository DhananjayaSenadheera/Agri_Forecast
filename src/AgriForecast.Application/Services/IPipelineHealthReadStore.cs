using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projections behind the "did the pipeline run?" endpoint. Separate from IIngestionReadStore
// on purpose: that store answers "what is ingestion doing right now" with no notion of a schedule, while
// everything here is scoped to a scheduled occurrence — the two nightly window reads below, plus the
// monthly macro freshness read.
public interface IPipelineHealthReadStore
{
    // Every run row belonging to a batch that has at least one row started inside
    // [fromUtc, toUtcExclusive). Rows OUTSIDE that range are returned too when their batch qualifies, so
    // the handler can see a batch's true first row and decide whether the batch really started in the
    // range rather than having spilled into it. FEATURE_BUILD rows are included (they carry their own
    // solo BatchId); every narrowing beyond this is the handler's policy call, because the batch and the
    // feature build are scoped to different windows.
    Task<IReadOnlyList<PipelineRunRow>> GetRunsForBatchesStartedBetweenAsync(
        DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default);

    // The verification for this night: the latest row linked to batchId, falling back to the latest row
    // stamped with pipelineDate when the batch has none (an ad-hoc verify run writes no BatchId).
    //
    // The fallback is bounded by notBeforeUtc — the night's fire time — because PipelineDate alone is
    // NOT specific to a night. An ad-hoc verify run against the same calendar date (three such rows
    // exist for 2026-07-26 in the live data) would otherwise become tonight's verdict, and a Fail from
    // a morning spot-check would paint a night that had not started yet as gate_blocked.
    Task<IngestionVerificationRow?> GetVerificationForBatchOrDateAsync(
        Guid? batchId, DateOnly pipelineDate, DateTime notBeforeUtc, CancellationToken ct = default);

    // The newest RetrievedAtUtc across the whole MacroSeriesPoints table, or NULL when it is empty.
    //
    // This is the only visible trace of the MONTHLY CBSL macro job (k8s/pipeline-monthly.yaml, "0 21 1 * *"
    // Asia/Colombo). A k8s job's exit status is invisible to the API, so freshness is read from the DATA
    // the job is supposed to produce rather than from a status nobody here can see.
    //
    // THE INVARIANT THIS RELIES ON, stated so it can be checked rather than assumed: a successful macro
    // pass re-upserts at least the newest bulletin it already holds, and the upsert's UPDATE branch stamps
    // RetrievedAtUtc = now on rows it has seen before (cbsl_macro/loader.py). So this instant moves forward
    // on every healthy pass, including the common month where CBSL published nothing new and zero rows were
    // INSERTED. If that ever stopped being true, this read would report stale during a genuinely quiet
    // month — annoying, and the safe direction to be wrong in.
    //
    // The other corollary is deliberate: a pass that exits 0 having written NOTHING (site restructure,
    // parser stopped matching, an empty artifact set) leaves this instant where it was and is reported as
    // stale. That is the point — this measures whether macro DATA is arriving, not whether a container
    // exited 0. The 2026-08-01 OOMKill was invisible for 15 days precisely because only exit codes were
    // being watched, and only for the nightly job.
    // Deliberately NOT window-scoped like the two reads above: the question is "how old is the newest
    // macro data we hold", not "did a particular occurrence run".
    Task<DateTime?> GetLatestMacroRetrievedAtUtcAsync(CancellationToken ct = default);
}

// One run row reduced to what the health derivation needs: which batch, which source, and whether it
// finished. Counts and coverage are deliberately absent — the banner never shows them.
public sealed record PipelineRunRow(
    Guid BatchId,
    string Source,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    IngestionRunStatus Status);
