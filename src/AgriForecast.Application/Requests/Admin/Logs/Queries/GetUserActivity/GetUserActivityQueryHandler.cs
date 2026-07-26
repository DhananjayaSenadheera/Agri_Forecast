using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// Server-paged account-event + admin-content-event history. Validation (bounds + known types) already
// ran in the pipeline, so this handler resolves the optional type filter(s) to enum values, pages via
// the store, and maps each row to the DTO (enum -> frozen wire string, UTC-kind stamp). The DB is behind ILogsReadStore for
// unit-testability. Mirrors GetIngestionRunsQueryHandler (manual mapping — no AutoMapper in the house).
public class GetUserActivityQueryHandler
    : IRequestHandler<GetUserActivityQuery, Result<UserActivityPage_GetDto>>
{
    private readonly ILogsReadStore _store;

    public GetUserActivityQueryHandler(ILogsReadStore store) => _store = store;

    public async Task<Result<UserActivityPage_GetDto>> Handle(
        GetUserActivityQuery request, CancellationToken cancellationToken)
    {
        var page = await _store.GetUserActivityPageAsync(
            request.Page, request.PageSize, ResolveTypeFilter(request), cancellationToken);

        var items = page.Items
            .Select(e => new UserActivity_GetDto
            {
                OccurredUtc = AsUtc(e.OccurredUtc),
                EventType = UserActivityEventStrings.ToWire(e.EventType),
                ActorUserId = e.ActorUserId,
                TargetUserId = e.TargetUserId,
                UsernameAttempted = e.UsernameAttempted,
                Details = e.Details
            })
            .ToList();

        var dto = new UserActivityPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<UserActivityPage_GetDto>.Success(dto);
    }

    // Collapses the two filter shapes into the ONE set the store takes.
    //
    // PRECEDENCE: a non-blank ?types= WINS and ?type= is ignored (documented on the query) — the
    // multi-select control replaces the single-select one, and intersecting them would silently
    // return an empty page whenever they disagree.
    //
    // Validation already ran in the pipeline, so every token here is known; TryParse is still used
    // (not assumed) and its nulls are dropped, so a hypothetical unvalidated caller degrades to a
    // narrower filter rather than throwing. Duplicates are harmless (the store distinct-ifies).
    // Returns null for "no filter" so an absent/blank parameter can never become an empty IN clause.
    private static IReadOnlyCollection<UserActivityEventType>? ResolveTypeFilter(GetUserActivityQuery request)
    {
        var tokens = UserActivityEventStrings.SplitTypes(request.Types);
        if (tokens.Count > 0)
        {
            var parsed = tokens
                .Select(UserActivityEventStrings.TryParse)
                .Where(t => t.HasValue)
                .Select(t => t!.Value)
                .Distinct()
                .ToList();

            return parsed.Count > 0 ? parsed : null;
        }

        var single = UserActivityEventStrings.TryParse(request.Type);
        return single.HasValue ? new[] { single.Value } : null;
    }

    // EF materializes datetime2 as DateTimeKind.Unspecified — stamp Kind=Utc so System.Text.Json
    // emits the trailing "Z" and the FE reads the instant as UTC (same as the ingestion reads).
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
