using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;
using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionStatus;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Admin ingestion observability: two read-only GETs backing the admin ingestion page — a health snapshot
// and a paged run history. Admin-locked at the controller level, because these surface operational
// internals (source states, error summaries, coverage windows) that farmers must not see.
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
}
