using AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;
using AgriForecast.Application.Requests.FestivalCalendar.Quaries.GetAll;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/festival-calendar")]
[Authorize]
public class FestivalCalendarController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Festival Calendar", message = error }
        }
    };

    // GET /api/festival-calendar/get/all -> all entries ordered by Date. Stays [Authorize]:
    // festival dates are non-personal reference data and the admin Festivals page is the only
    // consumer today; reads don't need the Admin gate (mirrors PolicyFlag / API-11 posture).
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new FestivalCalendarGetAllQuery());
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // Festival rows feed the forecasting model (as-of-joined lead-up windows) — every mutation is
    // Admin-only (API-9). Source is required and dates are kept date-only by the validator/mapper.
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] FestivalCalendarCreateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/festival-calendar/update -> full-object update keyed by id; Admin-only.
    // 200 with { id, trainingDataWarning } (warning non-null when the festival's Date — old or new
    // — is in the past); validation/guard failures -> 400 house error; not-found -> 400 failure.
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Update([FromBody] FestivalCalendarUpdateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/festival-calendar/delete/{id} -> Admin-only. Same { id, trainingDataWarning }
    // warning semantics as update: deleting a past-dated festival warns (retrains features).
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new FestivalCalendarDeleteCommand(id));
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
