namespace AgriForecast.Infrastructure.Services.CbslMacroIngestion;

public interface ICbslMacroIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
