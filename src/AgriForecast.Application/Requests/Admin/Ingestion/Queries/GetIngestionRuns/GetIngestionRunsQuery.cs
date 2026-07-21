using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// GET /api/admin/ingestion/runs?page=1&pageSize=20&source=. Server-paged run history, newest
// StartedUtc first. Admin-only (controller enforces the role); read-only. Bounds + the optional
// source are enforced by GetIngestionRunsValidator (bad values -> 400 via the validation pipeline).
public class GetIngestionRunsQuery : IRequest<Result<IngestionRunsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional source filter. When present it must be a known ingestion source key (validator).
    public string? Source { get; set; }
}
