using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;
using AgriForecast.Application.Requests.Admin.Ingestion.Commands.StopIngestionService;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;
using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Admin ingestion page backend: two read-only GETs (health snapshot + paged run history) and the two
// service-control POSTs behind the start/stop button. Admin-locked at the controller level, because these
// surface operational internals (source states, error summaries, coverage windows) that farmers must not
// see — and because the POSTs run the whole data pipeline.
[ApiController]
[Route("api/admin/ingestion")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminIngestionController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Ingestion", message = error }
        }
    };

    // The service-control endpoints answer conflicts with a flat, machine-readable { "error": "<code>" }
    // rather than the field-shaped validation envelope above: the admin UI switches on the code to choose
    // between "already running", "nothing to stop" and "started elsewhere, cannot stop from here".
    private static object ToConflictResponse(string error) => new { error };

    // GET /api/admin/ingestion/status — one at-a-glance ingestion-health snapshot.
    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var result = await mediator.Send(new GetIngestionStatusQuery());
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/admin/ingestion/runs?page=1&pageSize=20&source= — paged run history, newest first.
    // Bad page/pageSize/source -> 400 (GetIngestionRunsValidator). Empty page -> 200 with empty items.
    [HttpGet("runs")]
    public async Task<IActionResult> GetRuns(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? source = null)
    {
        var result = await mediator.Send(new GetIngestionRunsQuery
        {
            Page = page,
            PageSize = pageSize,
            Source = source
        });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/admin/ingestion/service/start — run one ingestion pass on this API host.
    // 202 { "batchId": "<guid>" } — accepted and launched; the pass runs in the BACKGROUND, so this
    //     returns in milliseconds while the pass takes minutes. Poll /runs with the batchId to follow it.
    // 409 { "error": "already_running" } — a pass already holds the cross-process application lock, or a
    //     fresh unfinished run row says one is in flight.
    // Deliberately 202, not 200: the work is accepted, not complete, and the response body cannot report
    // an outcome that has not happened yet.
    [HttpPost("service/start")]
    public async Task<IActionResult> StartService()
    {
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new StartIngestionServiceCommand { ActingUserId = actingId.Value });
        if (result.IsSuccess)
            return Accepted(result.Data);

        if (IngestionServiceControlErrors.IsConflict(result.Error))
            return Conflict(ToConflictResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/admin/ingestion/service/stop — ask the pass hosted on THIS process to stop.
    // 202 {} — cancellation signalled; the pass stops before its next source. Not "stopped": the API
    //     signals and returns, it never waits for the pass to unwind.
    // 409 { "error": "not_running" }   — nothing is running.
    // 409 { "error": "not_stoppable" } — something is running, but it was started by the scheduled worker
    //     in another process, which the API has no channel to cancel. Reported honestly rather than
    //     accepting a stop that would do nothing.
    [HttpPost("service/stop")]
    public async Task<IActionResult> StopService()
    {
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new StopIngestionServiceCommand { ActingUserId = actingId.Value });
        if (result.IsSuccess)
            // An empty object, not an empty body: the FE parses every 2xx as JSON.
            return Accepted(new { });

        if (IngestionServiceControlErrors.IsConflict(result.Error))
            return Conflict(ToConflictResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }
}
