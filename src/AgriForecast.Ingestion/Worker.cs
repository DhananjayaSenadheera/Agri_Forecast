using AgriForecast.Infrastructure.Services.MarketPriceIngestion;

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
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var ingestion = scope.ServiceProvider.GetRequiredService<IMarketPriceIngestionService>();

                _logger.LogInformation("Ingestion started");
                await ingestion.IngestAsync(stoppingToken);
                _logger.LogInformation("Ingestion finished");

            }
            catch (Exception ex)
            {
                _logger.LogError(ex.Message, "An error occurred during ingestion");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
            
        }
    }
}