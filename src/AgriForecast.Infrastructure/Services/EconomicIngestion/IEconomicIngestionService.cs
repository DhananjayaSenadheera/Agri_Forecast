using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.EconomicIngestion;

public interface IEconomicIngestionService
{
    // Returns run-tracking stats so the pass runner can put an HONEST status on the run row. The service is
    // fail-safe — an FX provider outage must not abort the other six sources — so it reports its own
    // Outcome=Failed rather than returning void and letting a swallowed fetch error render as a green row.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
