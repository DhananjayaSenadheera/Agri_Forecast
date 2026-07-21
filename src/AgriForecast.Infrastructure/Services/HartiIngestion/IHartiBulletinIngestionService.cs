using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.HartiIngestion;

public interface IHartiBulletinIngestionService
{
    // Returns the per-source counts (mapped from the Python /admin/ingest-harti response) the Worker
    // attaches to this source's IngestionRun row. Never throws to the Worker: an internal fail-safe
    // early-return still reports an EXPRESSIVE Outcome so the run row is honest — a disabled source
    // returns Outcome=Skipped, and a transport / non-200 / unparseable failure returns Outcome=Failed
    // with the same reason the watermark got (never a green Succeeded row).
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
