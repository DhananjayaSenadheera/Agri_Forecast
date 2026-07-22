using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services;
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
        var runs = scope.ServiceProvider.GetRequiredService<IIngestionRunRepository>();

        // One BatchId for the whole pass (config Ingestion:BatchId / env Ingestion__BatchId, else
        // generated). Every source's IngestionRun row shares it so the pass can be reconstructed.
        var batchId = IngestionRunAudit.ResolveBatchId(_configuration);
        _logger.LogInformation("Ingestion pass starting. BatchId={BatchId}", batchId);

        // Each source is wrapped by IngestionRunAudit: a Running row is committed before the source
        // runs, then transitioned to Succeeded (+ counts) / Failed (+ sanitized error). The audit
        // wrapper ALSO catches the source's exception (this is the per-source fail-isolation belt the
        // old per-block try/catch was) and can never let an audit write break the pass.

        // DAMBULLA_DEC market prices (reports counts).
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, "DAMBULLA_DEC", async ct =>
        {
            var ingestion = scope.ServiceProvider.GetRequiredService<IMarketPriceIngestionService>();
            _logger.LogInformation("Market price ingestion started");
            var stats = await ingestion.IngestAsync(ct);
            _logger.LogInformation("Market price ingestion finished");
            return stats;
        }, stoppingToken);

        // WEATHER (status-only — signature unchanged, so the run row carries null counts).
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, "WEATHER", async ct =>
        {
            var weather = scope.ServiceProvider.GetRequiredService<IWeatherIngestionService>();
            _logger.LogInformation("Weather ingestion started");
            await weather.IngestAsync(ct);
            _logger.LogInformation("Weather ingestion finished");
            return (IngestionRunStats?)null;
        }, stoppingToken);

        // ECONOMIC (status-only).
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, "ECONOMIC", async ct =>
        {
            var economic = scope.ServiceProvider.GetRequiredService<IEconomicIngestionService>();
            _logger.LogInformation("Economic ingestion started");
            await economic.IngestAsync(ct);
            _logger.LogInformation("Economic ingestion finished");
            return (IngestionRunStats?)null;
        }, stoppingToken);

        // NEWS (status-only).
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, "NEWS", async ct =>
        {
            var news = scope.ServiceProvider.GetRequiredService<INewsIngestionService>();
            _logger.LogInformation("News ingestion started");
            await news.IngestAsync(ct);
            _logger.LogInformation("News ingestion finished");
            return (IngestionRunStats?)null;
        }, stoppingToken);

        // R1.1 P1 Step 6: HARTI multi-market bulletin ingestion (reports counts). The service also
        // self-heals its watermark on internal failure (inner belt); the audit wrapper is the outer.
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, HartiBulletinIngestionService.SourceKey, async ct =>
        {
            var harti = scope.ServiceProvider.GetRequiredService<IHartiBulletinIngestionService>();
            _logger.LogInformation("HARTI ingestion started");
            var stats = await harti.IngestAsync(ct);
            _logger.LogInformation("HARTI ingestion finished");
            return (IngestionRunStats?)stats;
        }, stoppingToken);

        // CBSL Daily Price Report ingestion (feat/cbsl-price-parser — LIVE, capture-only): the
        // service orchestrates the Python parser via /admin/ingest-cbsl and reports counts. The
        // MarketPriceSources:Cbsl:Enabled flag remains the pause switch (flag off => Disabled
        // watermark, a deliberate no-op that is NOT a source failure, reported as Skipped).
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, CbslPriceReportIngestionService.SourceKey, async ct =>
        {
            var cbsl = scope.ServiceProvider.GetRequiredService<ICbslPriceReportIngestionService>();
            _logger.LogInformation("CBSL ingestion started");
            var stats = await cbsl.IngestAsync(ct);
            _logger.LogInformation("CBSL ingestion finished");
            return (IngestionRunStats?)stats;
        }, stoppingToken);

        // R1 P3 (86cahefbh): CBSL macro (CCPI/MEI vintage) ingestion. Feature-flagged OFF by default
        // (Disabled gating watermark, a deliberate no-op that is NOT a source failure). Status-only row.
        await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, CbslMacroIngestionService.SourceKey, async ct =>
        {
            var cbslMacro = scope.ServiceProvider.GetRequiredService<ICbslMacroIngestionService>();
            _logger.LogInformation("CBSL macro ingestion started");
            await cbslMacro.IngestAsync(ct);
            _logger.LogInformation("CBSL macro ingestion finished");
            return (IngestionRunStats?)null;
        }, stoppingToken);
    }
}
