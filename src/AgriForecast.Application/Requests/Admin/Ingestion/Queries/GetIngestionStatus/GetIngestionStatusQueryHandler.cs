using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// Builds the ingestion health snapshot. The DB is behind IIngestionReadStore and the config values
// behind IIngestionStatusSettings, so the state-derivation and batch-outcome roll-up are
// unit-testable with canned rows.
//
// STATE derivation (fail-safe — never a fake "running"):
//   * empty runs table                                  -> "unknown"
//   * a fresh unfinished run (StartedUtc within window)  -> "running"
//   * otherwise (a stale unfinished row = crashed, or all runs finished) -> "stopped"
// The staleness window is settings.RunningStalenessMinutes (default 120). "now" is DateTime.UtcNow
// (house style — the same convention the validators use); tests drive freshness via the run's
// StartedUtc relative to real now.
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

        var runCount = await _store.GetRunCountAsync(cancellationToken);

        string state;
        DateTime? lastRunAtUtc = null;
        string? lastRunStatus = null;

        if (runCount == 0)
        {
            state = "unknown";
        }
        else
        {
            var latestUnfinished = await _store.GetLatestUnfinishedStartedUtcAsync(cancellationToken);
            var freshThreshold = DateTime.UtcNow.AddMinutes(-stalenessMinutes);
            state = latestUnfinished.HasValue && latestUnfinished.Value >= freshThreshold
                ? "running"
                : "stopped";

            var latestRun = await _store.GetLatestRunAsync(cancellationToken);
            if (latestRun is not null)
            {
                lastRunAtUtc = latestRun.StartedUtc;
                var statuses = await _store.GetRunStatusesForBatchAsync(latestRun.BatchId, cancellationToken);
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

    // Roll-up of a batch's per-source statuses (PR-3 contract):
    //   all Succeeded/Skipped                 -> "succeeded"
    //   any Failed but not all                -> "partial"
    //   all Failed                            -> "failed"
    //   (a Running row present, no failures)  -> "partial" (in-flight, not yet cleanly succeeded)
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

    // EF materializes datetime2 as DateTimeKind.Unspecified, so System.Text.Json emits no trailing
    // "Z" and the FE's new Date(v) would treat these UTC instants as LOCAL. These columns are all
    // written as UTC (the audit writers stamp UtcNow), so stamp Kind=Utc here for the two admin
    // endpoints (a LOCAL fix, not a global converter — that stays out of scope).
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
