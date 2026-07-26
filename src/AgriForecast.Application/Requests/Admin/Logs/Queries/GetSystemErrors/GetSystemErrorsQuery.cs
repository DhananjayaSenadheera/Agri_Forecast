using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// GET /api/admin/logs/errors. Server-paged system-error history, newest OccurredUtc first (Id DESC
// tiebreak). Admin-only and read-only; the bounds are enforced by GetSystemErrorsValidator.
public class GetSystemErrorsQuery : IRequest<Result<SystemErrorsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
