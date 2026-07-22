using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// GET /api/admin/logs/user-activity?page=1&pageSize=20&type=. Server-paged account-event history,
// newest OccurredUtc first (Id DESC tiebreak). Admin-only (controller enforces the role); read-only.
// Bounds + the optional type are enforced by GetUserActivityValidator (bad values -> 400).
public class GetUserActivityQuery : IRequest<Result<UserActivityPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional event-type filter (frozen wire string). When present it must be a known type (validator).
    public string? Type { get; set; }
}
