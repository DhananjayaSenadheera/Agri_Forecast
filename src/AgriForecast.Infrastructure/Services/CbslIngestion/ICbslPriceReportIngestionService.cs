using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.CbslIngestion;

public interface ICbslPriceReportIngestionService
{
    // Returns run stats (counts + expressive Skipped/Failed outcome) so the Worker's
    // IngestionRun audit row carries honest numbers — mirrors IHartiBulletinIngestionService.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
