using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Per-source ingestion watermark store (R1.1 P1 Step 6). Services read the watermark to
// resume from the last SUCCESSFUL pass and write it back only on a terminal outcome
// (success / failure / disable). GetOrCreate is the self-healing entry point: a source with
// no watermark row yet gets one minted on first use, so a new source never has to be
// pre-seeded.
public interface IIngestionWatermarkRepository
{
    // Returns the watermark for a source, or null if none exists yet. Read-only.
    Task<IngestionWatermark?> GetAsync(string source, CancellationToken ct = default);

    // Returns the watermark for a source, creating (and persisting) one in the given initial
    // status if it does not exist yet. The returned entity is tracked so the caller can
    // transition it and Save. Idempotent per source (the Source column is unique).
    Task<IngestionWatermark> GetOrCreateAsync(
        string source,
        AgriForecast.Domain.Enums.IngestionSourceStatus initialStatus = AgriForecast.Domain.Enums.IngestionSourceStatus.Ok,
        string? initialMessage = null,
        CancellationToken ct = default);

    // Persists pending changes to a tracked watermark. Kept explicit (rather than folded into
    // the transition methods) so a caller can batch a transition with other unit-of-work writes.
    Task SaveChangesAsync(CancellationToken ct = default);
}
