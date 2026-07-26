using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// Builds the ingestion health snapshot. The DB is behind IIngestionReadStore and the config behind
// IIngestionStatusSettings, so the state derivation and batch roll-up are unit-testable.
//
// State: no qualifying runs -> "unknown"; a fresh unfinished run -> "running"; otherwise "stopped".
// A stale unfinished row counts as crashed, never as a fake "running". The staleness window is
// settings.RunningStalenessMinutes.
//
// state, lastRunAtUtc and lastRunStatus are all derived from ingestion runs only, excluding
// IngestionSources.ExcludedFromServiceState (today FEATURE_BUILD). The feature build runs last each day
// as a solo one-row batch, so without the exclusion lastRunStatus would sit at "succeeded" forever and a
// failed DAMBULLA_DEC / WEATHER / ECONOMIC / NEWS could never raise the FE's alarm; a hung feature build
// would likewise make the card read "running". The sources list and GET /runs are unaffected.
public class GetIngestionStatusQueryHandler
    : IRequestHandler<GetIngestionStatusQuery, Result<IngestionStatus_GetDto>>
{
    private readonly IIngestionReadStore _store;
    private readonly IIngestionStatusSettings _settings;

    public GetIngestionStatusQueryHandler(IIngestionReadStore store, IIngestionStatusSettings settings)
    {
        _store = store;
        _settings = settings;
    }

    public async Task<Result<IngestionStatus_GetDto>> Handle(
        GetIngestionStatusQuery request, CancellationToken cancellationToken)
    {
        var serviceAddress = _settings.ServiceAddress;
        var stalenessMinutes = _settings.RunningStalenessMinutes;

        // The one place the exclusion policy is chosen; the store just applies it.
        var excluded = IngestionSources.ExcludedFromServiceState;

        var runCount = await _store.GetRunCountAsync(excluded, cancellationToken);

        string state;
        DateTime? lastRunAtUtc = null;
        string? lastRunStatus = null;

        if (runCount == 0)
        {
            state = "unknown";
        }
        else
        {
            var latestUnfinished = await _store.GetLatestUnfinishedStartedUtcAsync(excluded, cancellationToken);
            var freshThreshold = DateTime.UtcNow.AddMinutes(-stalenessMinutes);
            state = latestUnfinished.HasValue && latestUnfinished.Value >= freshThreshold
                ? "running"
                : "stopped";

            var latestRun = await _store.GetLatestRunAsync(excluded, cancellationToken);
            if (latestRun is not null)
            {
                lastRunAtUtc = latestRun.StartedUtc;
                var statuses = await _store.GetRunStatusesForBatchAsync(
                    latestRun.BatchId, excluded, cancellationToken);
                lastRunStatus = AggregateBatchStatus(statuses);
            }
        }

        var verification = await _store.GetLatestVerificationAsync(cancellationToken);
        var watermarks = await _store.GetWatermarksAsync(cancellationToken);

        var dto = new IngestionStatus_GetDto
        {
            State = state,
            ServiceAddress = serviceAddress,
            LastRunAtUtc = AsUtc(lastRunAtUtc),
            LastRunStatus = lastRunStatus,
            LastVerification = verification is null
                ? null
                : new IngestionVerificationSummary_GetDto
                {
                    OverallStatus = IngestionStatusStrings.ToWire(verification.OverallStatus),
                    RanAtUtc = AsUtc(verification.RunUtc),
                    PipelineDate = Fmt(verification.PipelineDate),
                    NChecksPass = verification.NChecksPass,
                    NChecksWarn = verification.NChecksWarn,
                    NChecksFail = verification.NChecksFail
                },
            Sources = watermarks
                .Select(w => new IngestionSource_GetDto
                {
                    Source = w.Source,
                    Status = IngestionStatusStrings.ToWire(w.Status),
                    LastSuccessUtc = AsUtc(w.LastSuccessUtc),
                    LastObservedDate = w.LastObservedDate.HasValue ? Fmt(w.LastObservedDate.Value) : null,
                    LastMessage = w.LastMessage
                })
                .ToList()
        };

        return Result<IngestionStatus_GetDto>.Success(dto);
    }

    // Batch roll-up: all Succeeded/Skipped -> "succeeded"; some Failed -> "partial"; all Failed ->
    // "failed"; a Running row with no failures -> "partial", never a premature "succeeded".
    private static string? AggregateBatchStatus(IReadOnlyList<IngestionRunStatus> statuses)
    {
        if (statuses.Count == 0) return null;

        var anyFailed = statuses.Any(s => s == IngestionRunStatus.Failed);
        if (anyFailed)
            return statuses.All(s => s == IngestionRunStatus.Failed) ? "failed" : "partial";

        var allGood = statuses.All(s =>
            s == IngestionRunStatus.Succeeded || s == IngestionRunStatus.Skipped);
        return allGood ? "succeeded" : "partial";
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local. Stamp Kind=Utc here for the admin endpoints.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
