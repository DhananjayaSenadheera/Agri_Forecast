using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// Builds the ingestion health snapshot. The DB is behind IIngestionReadStore and the config values
// behind IIngestionStatusSettings, so the state-derivation and batch-outcome roll-up are
// unit-testable with canned rows.
//
// STATE derivation (fail-safe — never a fake "running"):
//   * no qualifying runs                                 -> "unknown"
//   * a fresh unfinished run (StartedUtc within window)  -> "running"
//   * otherwise (a stale unfinished row = crashed, or all runs finished) -> "stopped"
// The staleness window is settings.RunningStalenessMinutes (default 120). "now" is DateTime.UtcNow
// (house style — the same convention the validators use); tests drive freshness via the run's
// StartedUtc relative to real now.
//
// SOURCE SCOPE — every one of state / lastRunAtUtc / lastRunStatus is derived from ingestion runs
// ONLY, excluding IngestionSources.ExcludedFromServiceState (today: FEATURE_BUILD, the Python
// feature-build step that reuses the IngestionRuns table). This card answers "is the ingestion
// service healthy?", and the feature build is not that service.
//
// It is not cosmetic. The feature build runs LAST each day as a solo one-row batch, so unfiltered:
//   * lastRunStatus would roll up that solo batch and sit at "succeeded" permanently, so the FE's
//     red-dot alarm (lastRunStatus === 'failed') could NEVER fire for DAMBULLA_DEC / WEATHER /
//     ECONOMIC / NEWS — the four sources with no watermark row, visible only through this card;
//   * lastRunAtUtc would report the feature build's clock, not the ingestion pass's.
// GetLatestUnfinishedStartedUtcAsync is EXCLUDED TOO (a deliberate choice, not an oversight): a hung
// feature-build row would otherwise make the card read "Ingestion service is Running" while no
// ingestion is running at all, and state must describe the same thing lastRunAtUtc describes.
//
// The "sources" list is NOT affected — it is built from IngestionWatermarks, a different table that
// the feature build does not write. GET /runs is not affected either: FEATURE_BUILD rows are real
// run rows and stay listed (and filterable via ?source=FEATURE_BUILD).
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
