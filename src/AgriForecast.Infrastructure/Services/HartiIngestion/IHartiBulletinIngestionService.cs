namespace AgriForecast.Infrastructure.Services.HartiIngestion;

public interface IHartiBulletinIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
