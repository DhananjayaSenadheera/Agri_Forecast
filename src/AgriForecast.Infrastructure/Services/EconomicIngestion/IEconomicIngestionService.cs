namespace AgriForecast.Infrastructure.Services.EconomicIngestion;

public interface IEconomicIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
