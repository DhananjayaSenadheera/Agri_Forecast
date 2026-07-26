using AgriForecast.API.Extensions;
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

    // GET /api/festival-calendar/get/all -> all entries ordered by Date. Plain [Authorize]: festival dates
    // are non-personal reference data, so reads do not need the Admin gate.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new FestivalCalendarGetAllQuery());
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // Festival rows feed the forecasting model, so every mutation is Admin-only. Source is required and the
    // dates are kept date-only by the validator and mapper.
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] FestivalCalendarCreateCommand command)
    {
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/festival-calendar/update -> full-object update keyed by id; Admin-only. 200 with
    // { id, trainingDataWarning }; the warning is non-null when the old or new Date is in the past.
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Update([FromBody] FestivalCalendarUpdateCommand command)
    {
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/festival-calendar/delete/{id} -> Admin-only. Same warning semantics as update: deleting a
    // past-dated festival warns.
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Delete(Guid id)
    {
        // The acting admin comes from the JWT, never the route, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new FestivalCalendarDeleteCommand(id, actingId.Value));
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
