using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StopIngestionService;

// Signals cancellation to the ingestion pass hosted by THIS API process.
//
// The three-way answer is the honest part of the contract:
//   accepted      — a pass started here was signalled; it stops before its next source.
//   not_stoppable — something IS running, but not here. The scheduled CronJob/Docker worker has no inbound
//                   control channel, so the API cannot reach it. Saying so beats returning 202 and letting
//                   the admin watch a "stopped" service keep ingesting.
//   not_running   — nothing is running at all.
//
// The API-hosted registry is consulted FIRST because it is the only thing that can actually be stopped;
// the DB-derived state is only used to tell the two refusals apart.
public class StopIngestionServiceCommandHandler
    : IRequestHandler<StopIngestionServiceCommand, Result<Unit>>
{
    private readonly IApiHostedIngestionPasses _hostedPasses;
    private readonly IIngestionReadStore _store;
    private readonly IIngestionStatusSettings _settings;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<StopIngestionServiceCommandHandler> _logger;

    public StopIngestionServiceCommandHandler(
        IApiHostedIngestionPasses hostedPasses,
        IIngestionReadStore store,
        IIngestionStatusSettings settings,
        IUserActivityAudit activityAudit,
        ILogger<StopIngestionServiceCommandHandler> logger)
    {
        _hostedPasses = hostedPasses;
        _store = store;
        _settings = settings;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<Unit>> Handle(
        StopIngestionServiceCommand request, CancellationToken cancellationToken)
    {
        if (_hostedPasses.TryRequestStop(out var batchId))
        {
            _logger.LogInformation("Stop requested for API-hosted ingestion pass {BatchId} by admin {ActorUserId}.",
                batchId, request.ActingUserId);

            // Audited AFTER the cancellation was signalled, and fail-open — an unwritable audit row must
            // not turn an accepted stop into an error.
            await _activityAudit.RecordIngestionServiceStopRequestedAsync(
                request.ActingUserId, batchId, cancellationToken);

            return Result<Unit>.Success(Unit.Value);
        }

        // Nothing hosted here. Is anything running at all?
        var runningElsewhere = await StartIngestionServiceCommandHandler.IsRunningPerRunRowsAsync(
            _store, _settings, cancellationToken);

        if (runningElsewhere)
        {
            _logger.LogInformation(
                "Stop refused: a pass is running, but it was not started by this API process and cannot be cancelled from here.");
            return Result<Unit>.Failure(IngestionServiceControlErrors.NotStoppable);
        }

        return Result<Unit>.Failure(IngestionServiceControlErrors.NotRunning);
    }
}
