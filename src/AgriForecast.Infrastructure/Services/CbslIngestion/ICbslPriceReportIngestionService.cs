namespace AgriForecast.Infrastructure.Services.CbslIngestion;

public interface ICbslPriceReportIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
