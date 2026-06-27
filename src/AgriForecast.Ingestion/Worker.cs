using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;

namespace AgriForecast.Ingestion;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _appLifetime;

    public Worker(
        ILogger<Worker> logger,
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _appLifetime = appLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Run-once mode: a single pass of both ingestions, then exit cleanly.
        // Driven by Ingestion:RunOnce (env Ingestion__RunOnce) or the simple RUN_ONCE env var.
        var runOnce = _configuration.GetValue<bool>("Ingestion:RunOnce")
                      || _configuration.GetValue<bool>("RUN_ONCE");

        if (runOnce)
        {
            _logger.LogInformation("Ingestion running in RunOnce mode: one pass then exit");
            await RunPassAsync(stoppingToken);
            _logger.LogInformation("RunOnce pass complete. Stopping application");
            _appLifetime.StopApplication();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunPassAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();

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

        try
        {
            var economic = scope.ServiceProvider.GetRequiredService<IEconomicIngestionService>();
            _logger.LogInformation("Economic ingestion started");
            await economic.IngestAsync(stoppingToken);
            _logger.LogInformation("Economic ingestion finished");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during economic ingestion");
        }
    }
}
