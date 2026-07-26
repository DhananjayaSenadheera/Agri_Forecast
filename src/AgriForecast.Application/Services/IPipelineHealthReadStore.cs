using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over IngestionRuns / IngestionVerifications for the "did last night's pipeline
// run?" endpoint. Separate from IIngestionReadStore on purpose: that store answers "what is ingestion
// doing right now" with no notion of a scheduled window, while these two reads are both window-scoped.
public interface IPipelineHealthReadStore
{
    // Every run row belonging to a batch that has at least one row started inside [fromUtc, toUtc].
    // Rows OUTSIDE the window are returned too when their batch qualifies, so the handler can see a
    // batch's true first row and decide whether the batch really started in the window rather than
    // having spilled into it. FEATURE_BUILD rows are included (they carry their own solo BatchId);
    // filtering them is the handler's policy call.
    Task<IReadOnlyList<PipelineRunRow>> GetRunsForBatchesStartedBetweenAsync(
        DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    // The verification for this night: the latest row linked to batchId, falling back to the latest row
    // stamped with pipelineDate when the batch has none (an ad-hoc verify run writes no BatchId).
    Task<IngestionVerificationRow?> GetVerificationForBatchOrDateAsync(
        Guid? batchId, DateOnly pipelineDate, CancellationToken ct = default);
}

// One run row reduced to what the health derivation needs: which batch, which source, and whether it
// finished. Counts and coverage are deliberately absent — the banner never shows them.
public sealed record PipelineRunRow(
    Guid BatchId,
    string Source,
    DateTime StartedUtc,
    DateTime? FinishedUtc,
    IngestionRunStatus Status);
