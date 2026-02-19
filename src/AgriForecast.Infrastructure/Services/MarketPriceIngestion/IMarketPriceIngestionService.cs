namespace AgriForecast.Infrastructure.Services.MarketPriceIngestion;

public interface IMarketPriceIngestionService
{
    Task IngestAsync(CancellationToken ct);
}