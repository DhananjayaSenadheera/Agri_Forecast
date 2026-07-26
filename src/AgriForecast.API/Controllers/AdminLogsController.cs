using AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Admin Logs hub (PR A). Two READ-ONLY GETs backing the admin Logs page: model-training history and
// the user-activity audit trail. Entirely Admin-locked at the controller level (these surface
// operational internals — model-promotion decisions and security events — that farmers must not see).
// Mirrors AdminIngestionController posture: full-literal routes, structured error shape, no stack
// traces leaked (validation failures become a 400 via the ValidationBehavior pipeline).
[ApiController]
[Route("api/admin/logs")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminLogsController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Logs", message = error }
        }
    };

    // GET /api/admin/logs/training?page=1&pageSize=20 — paged model-training history, newest first.
    // Bad page/pageSize -> 400 (GetTrainingRunsValidator). Empty page -> 200 with empty items.
    [HttpGet("training")]
    public async Task<IActionResult> GetTraining(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await mediator.Send(new GetTrainingRunsQuery
        {
            Page = page,
            PageSize = pageSize
        });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/admin/logs/user-activity?page=1&pageSize=20&type=&types= — paged account-event and
    // admin-content-event history, newest first.
    //   type=<wire>              single event type (unchanged)
    //   types=<wire>,<wire>,...  comma-separated set, OR-combined; EVERY token must be known
    // When both are given, types WINS. Bad page/pageSize/type/types -> 400
    // (GetUserActivityValidator). Empty page -> 200 with empty items.
    [HttpGet("user-activity")]
    public async Task<IActionResult> GetUserActivity(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? type = null,
        [FromQuery] string? types = null)
    {
        var result = await mediator.Send(new GetUserActivityQuery
        {
            Page = page,
            PageSize = pageSize,
            Type = type,
            Types = types
        });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/admin/logs/errors?page=1&pageSize=20 — paged unhandled-500 history, newest first. Bad
    // page/pageSize -> 400 (GetSystemErrorsValidator). Empty page -> 200 with empty items.
    [HttpGet("errors")]
    public async Task<IActionResult> GetSystemErrors(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var result = await mediator.Send(new GetSystemErrorsQuery
        {
            Page = page,
            PageSize = pageSize
        });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
