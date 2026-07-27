using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.HartiIngestion;

public interface IHartiBulletinIngestionService
{
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
