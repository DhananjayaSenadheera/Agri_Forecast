using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;

// POST /api/admin/ingestion/service/start — admin-triggered ingestion pass.
// The command carries no caller-supplied data at all: the batchId is minted server-side and the acting
// admin comes from the JWT, so there is nothing here a request body could influence.
public class StartIngestionServiceCommand : IRequest<Result<IngestionServiceStart_Dto>>
{
    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}

// 202 payload. batchId is the pass's IngestionRuns.BatchId, so the admin UI can immediately poll
// GET /api/admin/ingestion/runs and follow exactly the pass it just started.
public class IngestionServiceStart_Dto
{
    public Guid BatchId { get; set; }
}
