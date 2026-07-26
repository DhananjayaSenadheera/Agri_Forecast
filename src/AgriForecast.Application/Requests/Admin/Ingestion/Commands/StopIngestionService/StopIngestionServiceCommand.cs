using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Commands.StopIngestionService;

// POST /api/admin/ingestion/service/stop — asks the pass hosted on THIS API process to stop.
// Success carries no payload (the wire body is {}), because there is nothing honest to report yet: the
// API has signalled cancellation, it has not witnessed the pass end.
public class StopIngestionServiceCommand : IRequest<Result<Unit>>
{
    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
