using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;

namespace AgriForecast.Ingestion;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;

    public Worker(ILogger<Worker> logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using (var scope = _serviceProvider.CreateScope())
            {
                try
                {
                    var ingestion = scope.ServiceProvider.GetRequiredService<IMarketPriceIngestionService>();
                    _logger.LogInformation("Market price ingestion started");
                    await ingestion.IngestAsync(stoppingToken);
                    _logger.LogInformation("Market price ingestion finished");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during market price ingestion");
                }

                try
                {
                    var weather = scope.ServiceProvider.GetRequiredService<IWeatherIngestionService>();
                    _logger.LogInformation("Weather ingestion started");
                    await weather.IngestAsync(stoppingToken);
                    _logger.LogInformation("Weather ingestion finished");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occurred during weather ingestion");
                }
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            
        }
    }
}