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
    private readonly IIngestionPassLock _passLock;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _appLifetime;

    public Worker(
        ILogger<Worker> logger,
        IIngestionPassRunner passRunner,
        IIngestionPassLock passLock,
        IConfiguration configuration,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _passRunner = passRunner;
        _passLock = passLock;
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
    //
    // The pass is taken under the SAME cross-process application lock the API's admin start button uses.
    // Without this, the lock guarded only one of its two callers and the 21:00 job could run straight over
    // an admin-started pass — double-fetching every source and interleaving watermark writes.
    private async Task RunPassAsync(CancellationToken stoppingToken)
    {
        var batchId = IngestionRunAudit.ResolveBatchId(_configuration);

        // SKIP, never queue or retry. An admin pass is already covering today's data, and the DEC source
        // fetches full history each pass, so anything this run would have picked up lands on the next one.
        // The CronJob's startingDeadlineSeconds already handles genuine missed schedules; blocking here
        // would just park a second pass behind the first and run it against a stale batchId.
        await using var lease = await _passLock.TryAcquireAsync(stoppingToken);
        if (lease is null)
        {
            _logger.LogWarning(
                "Ingestion pass {BatchId} SKIPPED: another host already holds the ingestion pass lock "
                + "(an admin-started pass on the API, or an overlapping scheduled run). No pass was run.",
                batchId);
            return;
        }

        await _passRunner.RunPassAsync(batchId, stoppingToken);
    }
}
