using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// Server-paged account and admin-content event history. Resolves the optional type filters to enum
// values, pages via ILogsReadStore, and maps each row to the DTO by hand.
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

    // Collapses the two filter shapes into the one set the store takes. A non-blank ?types= wins and
    // ?type= is ignored. TryParse nulls are dropped rather than assumed away, so an unvalidated caller
    // degrades to a narrower filter instead of throwing. Returns null for "no filter", so an absent
    // parameter can never become an empty IN clause.
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

    // EF materializes datetime2 as DateTimeKind.Unspecified, so stamp Kind=Utc for the trailing "Z".
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
