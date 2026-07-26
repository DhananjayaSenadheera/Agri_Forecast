using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.HartiIngestion;

public interface IHartiBulletinIngestionService
{
    // Returns the per-source counts the Worker attaches to this source's IngestionRun row. Never throws to
    // the Worker: a fail-safe early return still reports an expressive Outcome, so a disabled source is
    // Skipped and a transport or parse failure is Failed with the same reason the watermark got — never a
    // green Succeeded row.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
