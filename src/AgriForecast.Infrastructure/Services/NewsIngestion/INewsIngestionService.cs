namespace AgriForecast.Infrastructure.Services.NewsIngestion;

public interface INewsIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
