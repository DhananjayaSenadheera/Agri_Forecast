using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.NewsIngestion;
using AgriForecast.Infrastructure.Services.HartiIngestion;
using AgriForecast.Infrastructure.Services.CbslIngestion;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;

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

        try
        {
            var news = scope.ServiceProvider.GetRequiredService<INewsIngestionService>();
            _logger.LogInformation("News ingestion started");
            await news.IngestAsync(stoppingToken);
            _logger.LogInformation("News ingestion finished");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during news ingestion");
        }

        // R1.1 P1 Step 6: HARTI multi-market bulletin ingestion. Fail-isolated exactly like the
        // blocks above — a HARTI failure NEVER aborts the pass; it is logged ERROR and the pass
        // continues. The service itself also self-heals its watermark on failure (inner belt).
        try
        {
            var harti = scope.ServiceProvider.GetRequiredService<IHartiBulletinIngestionService>();
            _logger.LogInformation("HARTI ingestion started");
            await harti.IngestAsync(stoppingToken);
            _logger.LogInformation("HARTI ingestion finished");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during HARTI ingestion");
        }

        // R1.1 P1 Step 6: CBSL daily price report ingestion. Feature-flagged OFF by default
        // (Disabled watermark, a deliberate no-op that is NOT a source failure). Still wrapped in
        // its own try/catch so that, once enabled, a CBSL failure is isolated like every other source.
        try
        {
            var cbsl = scope.ServiceProvider.GetRequiredService<ICbslPriceReportIngestionService>();
            _logger.LogInformation("CBSL ingestion started");
            await cbsl.IngestAsync(stoppingToken);
            _logger.LogInformation("CBSL ingestion finished");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during CBSL ingestion");
        }

        // R1 P3 (86cahefbh): CBSL macro (CCPI/MEI vintage) ingestion. Feature-flagged OFF by default
        // (Disabled gating watermark, a deliberate no-op that is NOT a source failure). Wrapped in
        // its own try/catch so that, once enabled, a macro failure is isolated like every other source.
        try
        {
            var cbslMacro = scope.ServiceProvider.GetRequiredService<ICbslMacroIngestionService>();
            _logger.LogInformation("CBSL macro ingestion started");
            await cbslMacro.IngestAsync(stoppingToken);
            _logger.LogInformation("CBSL macro ingestion finished");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred during CBSL macro ingestion");
        }
    }
}
