namespace AgriForecast.Infrastructure.Services.WeatherIngestion;

public interface IWeatherIngestionService
{
    Task IngestAsync(CancellationToken ct);
}
