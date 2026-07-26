using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Per-source ingestion watermark store. Services resume from the last successful pass and write back
// only on a terminal outcome. GetOrCreate mints a row on first use, so a new source needs no seeding.
public interface IIngestionWatermarkRepository
{
    Task<IngestionWatermark?> GetAsync(string source, CancellationToken ct = default);

    // Creates the row in the given initial status if it does not exist. Returns a tracked entity so the
    // caller can transition it and save. Idempotent per source (Source is unique).
    Task<IngestionWatermark> GetOrCreateAsync(
        string source,
        AgriForecast.Domain.Enums.IngestionSourceStatus initialStatus = AgriForecast.Domain.Enums.IngestionSourceStatus.Ok,
        string? initialMessage = null,
        CancellationToken ct = default);

    // Kept explicit so a caller can batch a watermark transition with other unit-of-work writes.
    Task SaveChangesAsync(CancellationToken ct = default);
}
