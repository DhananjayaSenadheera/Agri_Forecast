using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.MarketPriceIngestion;

public interface IMarketPriceIngestionService
{
    // Returns the per-source counts (inserted / skipped / distinct crops) the Worker attaches to
    // this source's IngestionRun row. These are the same numbers the service already logs — not a
    // recount.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}