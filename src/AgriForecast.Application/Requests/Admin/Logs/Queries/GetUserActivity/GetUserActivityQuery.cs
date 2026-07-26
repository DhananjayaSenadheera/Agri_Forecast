using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// GET /api/admin/logs/user-activity?page=1&pageSize=20&type=&types=. Server-paged account-event +
// admin-content-event history, newest OccurredUtc first (Id DESC tiebreak). Admin-only (controller
// enforces the role); read-only. Bounds + the optional filters are enforced by
// GetUserActivityValidator (bad values -> 400).
public class GetUserActivityQuery : IRequest<Result<UserActivityPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional SINGLE event-type filter (frozen wire string). When present it must be a known type
    // (validator). Behaviour unchanged — the existing FE call keeps working exactly as before.
    public string? Type { get; set; }

    // Optional MULTI event-type filter: a comma-separated list of frozen wire strings, OR-combined
    // (e.g. "policyFlagChanged,festivalChanged" returns rows of either type). EVERY token must be a
    // known type or the whole request is a 400 — a partly-valid list is never silently narrowed,
    // because a filter that quietly drops a term shows the admin a page they would read as complete.
    //
    // PRECEDENCE: when BOTH are supplied, `types` WINS and `type` is ignored. A multi-select FE
    // control replaces the single-select one rather than composing with it; intersecting them would
    // produce a confusing empty page whenever they disagree.
    public string? Types { get; set; }
}
