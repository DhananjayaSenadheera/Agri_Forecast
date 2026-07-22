using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// GET /api/admin/logs/errors?page=1&pageSize=20. Server-paged system-error history, newest OccurredUtc
// first (Id DESC tiebreak). Admin-only (controller enforces the role); read-only. Bounds are enforced by
// GetSystemErrorsValidator (bad values -> 400 via the validation pipeline).
public class GetSystemErrorsQuery : IRequest<Result<SystemErrorsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
