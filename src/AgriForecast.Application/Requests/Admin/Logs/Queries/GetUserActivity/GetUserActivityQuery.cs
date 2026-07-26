using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// GET /api/admin/logs/user-activity. Server-paged account and admin-content event history, newest
// OccurredUtc first (Id DESC tiebreak). Admin-only and read-only; the bounds and optional filters are
// enforced by GetUserActivityValidator.
public class GetUserActivityQuery : IRequest<Result<UserActivityPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional single event-type filter; when present it must be a known wire string.
    public string? Type { get; set; }

    // Optional multi event-type filter: a comma-separated list of wire strings, OR-combined. Every token
    // must be known or the whole request is a 400 — a partly-valid list is never silently narrowed, because
    // a filter that quietly drops a term shows the admin a page they would read as complete.
    // When both are supplied, types wins and type is ignored.
    public string? Types { get; set; }
}
