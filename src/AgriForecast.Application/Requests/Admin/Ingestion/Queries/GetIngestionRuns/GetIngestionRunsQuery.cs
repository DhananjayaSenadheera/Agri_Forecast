using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// GET /api/admin/ingestion/runs. Server-paged run history, newest StartedUtc first. Admin-only and
// read-only; the bounds and the optional source are enforced by GetIngestionRunsValidator.
public class GetIngestionRunsQuery : IRequest<Result<IngestionRunsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional source filter. When present it must be a known ingestion source key (validator).
    public string? Source { get; set; }
}
