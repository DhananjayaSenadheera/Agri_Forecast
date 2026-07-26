using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Delete;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Update;
using AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/policy-flag")]
[Authorize]
public class PolicyFlagController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Policy Flag", message = error }
        }
    };

    // Policy flags feed the forecasting model (as-of joins) — creating one is Admin-only (API-9).
    // The GET below stays [Authorize] (any authenticated user may read active flags).
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] PolicyFlagCreateCommand command)
    {
        // Stamp the acting admin from the JWT (never the body): the audit row must name who really
        // made the change, and a body-supplied ActingUserId is discarded here.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/policy-flag/update -> full-object update keyed by id; Admin-only (as-of-joined ML data).
    // 200 with { id, trainingDataWarning } on success (warning non-null when the flag's window — old
    // or new — is in the past); validation/guard failures -> 400 house error; not-found -> 400 failure.
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Update([FromBody] PolicyFlagUpdateCommand command)
    {
        // Stamp the acting admin from the JWT (never the body): the audit row must name who really
        // made the change, and a body-supplied ActingUserId is discarded here.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/policy-flag/delete/{id} -> Admin-only. Same training-data warning semantics as update.
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Delete(Guid id)
    {
        // Stamp the acting admin from the JWT, never the route: the audit row must name who really
        // deleted the row.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new PolicyFlagDeleteCommand(id, actingId.Value));
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/policy-flag/get/all            -> all flags ordered by EffectiveFrom
    // GET /api/policy-flag/get/all?asOfDate=  -> only flags active on that date
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll([FromQuery] DateTime? asOfDate)
    {
        var result = await mediator.Send(new PolicyFlagGetAllQuery { AsOfDate = asOfDate });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
