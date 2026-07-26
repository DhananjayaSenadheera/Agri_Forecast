using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;

// Starts one ingestion pass on this API host and answers immediately with its batchId.
//
// Two guards, in this order and for different reasons:
//  1. A fresh unfinished run row -> already_running. This is the FRIENDLY pre-check. It reads the same
//     signal the admin status card shows, so the button and the card can never disagree, and it costs one
//     indexed query instead of a connection. It is NOT the mutex: run rows are best-effort audit writes.
//  2. The SQL application lock -> already_running. This is the REAL mutex, and the only one that can see
//     the scheduled worker in another process. Nothing runs unless this is granted.
//
// The pass itself is launched in the background: it takes minutes, the admin gets 202 in milliseconds. It
// runs under the pass handle's OWN token (never the request's, which dies with the response), and the
// handle plus the lock lease are disposed in a finally so a crashed pass cannot wedge every later one.
public class StartIngestionServiceCommandHandler
    : IRequestHandler<StartIngestionServiceCommand, Result<IngestionServiceStart_Dto>>
{
    private readonly IIngestionReadStore _store;
    private readonly IIngestionStatusSettings _settings;
    private readonly IIngestionPassLock _passLock;
    private readonly IApiHostedIngestionPasses _hostedPasses;
    private readonly IIngestionPassRunner _runner;
    private readonly IBackgroundWorkLauncher _launcher;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<StartIngestionServiceCommandHandler> _logger;

    public StartIngestionServiceCommandHandler(
        IIngestionReadStore store,
        IIngestionStatusSettings settings,
        IIngestionPassLock passLock,
        IApiHostedIngestionPasses hostedPasses,
        IIngestionPassRunner runner,
        IBackgroundWorkLauncher launcher,
        IUserActivityAudit activityAudit,
        ILogger<StartIngestionServiceCommandHandler> logger)
    {
        _store = store;
        _settings = settings;
        _passLock = passLock;
        _hostedPasses = hostedPasses;
        _runner = runner;
        _launcher = launcher;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<IngestionServiceStart_Dto>> Handle(
        StartIngestionServiceCommand request, CancellationToken cancellationToken)
    {
        // Guard 1 — the same derivation the status card uses, including the FEATURE_BUILD exclusion, so a
        // hung Python feature build never blocks the start button.
        if (await IsRunningPerRunRowsAsync(_store, _settings, cancellationToken))
        {
            _logger.LogInformation(
                "Ingestion start refused: an unfinished run row is younger than the {Minutes}-minute staleness window.",
                _settings.RunningStalenessMinutes);
            return Result<IngestionServiceStart_Dto>.Failure(IngestionServiceControlErrors.AlreadyRunning);
        }

        // Guard 2 — the cross-process mutex. Fails fast (no wait) when a pass holds it anywhere.
        var lease = await _passLock.TryAcquireAsync(cancellationToken);
        if (lease is null)
        {
            _logger.LogInformation("Ingestion start refused: the pass application lock is held by another host.");
            return Result<IngestionServiceStart_Dto>.Failure(IngestionServiceControlErrors.AlreadyRunning);
        }

        var batchId = Guid.NewGuid();
        IApiHostedPassHandle handle;
        try
        {
            handle = _hostedPasses.Begin(batchId);
        }
        catch
        {
            // Never hold the lock for a pass that was never started.
            await lease.DisposeAsync();
            throw;
        }

        _launcher.Run(async () =>
        {
            try
            {
                await _runner.RunPassAsync(batchId, handle.Token);
            }
            finally
            {
                // Order matters: de-register first so a stop arriving now is answered honestly, then give
                // the lock back so the next pass (here or on the worker) can start.
                handle.Dispose();
                await lease.DisposeAsync();
            }
        });

        _logger.LogInformation("Ingestion pass {BatchId} started by admin {ActorUserId}.",
            batchId, request.ActingUserId);

        // Audited AFTER the pass was accepted and launched, and the audit swallows-and-logs, so an
        // unwritable audit row can never turn a started pass into an error the admin sees.
        await _activityAudit.RecordIngestionServiceStartedAsync(
            request.ActingUserId, batchId, cancellationToken);

        return Result<IngestionServiceStart_Dto>.Success(new IngestionServiceStart_Dto { BatchId = batchId });
    }

    // Shared by start and stop: "running" = an unfinished run row younger than the staleness window,
    // ignoring the sources excluded from the service state. One definition, so the two endpoints and the
    // status card can never tell the admin three different stories.
    internal static async Task<bool> IsRunningPerRunRowsAsync(
        IIngestionReadStore store, IIngestionStatusSettings settings, CancellationToken ct)
    {
        var latestUnfinished = await store.GetLatestUnfinishedStartedUtcAsync(
            IngestionSources.ExcludedFromServiceState, ct);

        return latestUnfinished.HasValue
               && latestUnfinished.Value >= DateTime.UtcNow.AddMinutes(-settings.RunningStalenessMinutes);
    }
}
