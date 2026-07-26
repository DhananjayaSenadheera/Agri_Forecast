using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.NewsIngestion;

public interface INewsIngestionService
{
    // Returns run-tracking stats so the pass runner can put an HONEST status on the run row. This source is
    // fail-safe — it does not throw a transport or HTTP error at its caller — so without a returned
    // Outcome=Failed a dead or erroring ML service silently produced a green NEWS row.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
