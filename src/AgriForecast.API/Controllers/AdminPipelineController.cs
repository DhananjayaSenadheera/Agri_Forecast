using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// The nightly data pipeline as an operator sees it. Separate from AdminIngestionController because that
// one describes the ingestion SERVICE (is it running, what can I stop, per-source watermarks) while this
// one describes one scheduled NIGHT end to end, feature build included.
// Admin-locked at the controller level: it exposes scheduling and failure detail farmers must not see.
[ApiController]
[Route("api/admin/pipeline")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminPipelineController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Pipeline", message = error }
        }
    };

    // GET /api/admin/pipeline/health — did last night's run happen, and how did it end?
    // Always 200 for an admin: "nothing ran" is an answer (state "missing"), not an error.
    [HttpGet("health")]
    public async Task<IActionResult> GetHealth()
    {
        var result = await mediator.Send(new GetPipelineHealthQuery());
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
