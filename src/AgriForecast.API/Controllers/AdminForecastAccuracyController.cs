using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;
using AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Admin "Forecast accuracy": read-only GETs over the frozen forecast ledger (ForecastSnapshots) — how
// the served predictions actually scored once their harvest dates passed. Admin-locked at the controller
// level like the rest of the admin surface: these responses expose model-vs-fallback performance and
// per-version skill, which is operator information, not farmer information.
[ApiController]
[Route("api/admin/forecast-accuracy")]
[Authorize(Roles = UserRoles.Admin)]
public class AdminForecastAccuracyController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "ForecastAccuracy", message = error }
        }
    };

    // GET /api/admin/forecast-accuracy/summary — state counts plus the accuracy aggregates, split by
    // active predictor and by (model version, active predictor). No parameters: the full matured history
    // is the denominator. An empty table is a 200 with zero counts and empty groups.
    [HttpGet("summary")]
    public async Task<IActionResult> GetSummary()
    {
        var result = await mediator.Send(new GetForecastAccuracySummaryQuery());
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/admin/forecast-accuracy/snapshots?page=1&pageSize=20&cropId=&modelVersion=&maturedOnly=
    // Paged snapshot ledger, newest snapshot date first. Filters are AND-combined and optional.
    // Bad page/pageSize/cropId/modelVersion -> 400 (GetForecastSnapshotsValidator). A filter that matches
    // nothing, or a page past the end, is a 200 with empty items.
    [HttpGet("snapshots")]
    public async Task<IActionResult> GetSnapshots(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] Guid? cropId = null,
        [FromQuery] string? modelVersion = null,
        [FromQuery] bool maturedOnly = false)
    {
        var result = await mediator.Send(new GetForecastSnapshotsQuery
        {
            Page = page,
            PageSize = pageSize,
            CropId = cropId,
            ModelVersion = modelVersion,
            MaturedOnly = maturedOnly
        });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
