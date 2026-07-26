using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;

// GET /api/admin/ingestion/status. No inputs — a whole-system snapshot. Admin-only and read-only.
public class GetIngestionStatusQuery : IRequest<Result<IngestionStatus_GetDto>>
{
}
