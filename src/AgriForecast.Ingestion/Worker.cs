using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Services;

namespace AgriForecast.Ingestion;

// Schedules ingestion passes. The pass ITSELF — the source sequence, the per-source run rows, the
// fail-isolation — no longer lives here: it was lifted into IIngestionPassRunner so the admin start button
// on the API runs exactly the same code path. This class is now only "when to run", never "what to run".
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IIngestionPassRunner _passRunner;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _appLifetime;

    public Worker(
        ILogger<Worker> logger,
        IIngestionPassRunner passRunner,
        IConfiguration configuration,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _passRunner = passRunner;
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

    // One BatchId for the whole pass (config Ingestion:BatchId / env Ingestion__BatchId, else generated).
    // Every source's IngestionRun row shares it so the pass can be reconstructed. Resolving it here rather
    // than inside the runner keeps the runner free of configuration: the API mints a fresh GUID per pass,
    // while this host lets an orchestrator pin one.
    private Task RunPassAsync(CancellationToken stoppingToken)
    {
        var batchId = IngestionRunAudit.ResolveBatchId(_configuration);
        return _passRunner.RunPassAsync(batchId, stoppingToken);
    }
}
