using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.NewsEvents.Commands.Create;
using AgriForecast.Application.Requests.NewsEvents.Commands.Delete;
using AgriForecast.Application.Requests.NewsEvents.Commands.Update;
using AgriForecast.Application.Requests.NewsEvents.Quaries.GetAll;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/news-events")]
[Authorize]
public class NewsEventController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "News Event", message = error }
        }
    };

    // GET /api/news-events/get/all -> all events, newest knowledge date first. Plain [Authorize], not
    // Admin-gated: non-personal reference data. Returns 200 [] on empty, not the legacy 400-on-empty.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new NewsEventGetAllQuery());
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // News events are curated data mutated by admins only. Capture and storage only: unlike PolicyFlag and
    // FestivalCalendar these are not ML feature inputs yet, so there is deliberately no training-data warning
    // on the mutations. PublishedAt is the immutable vintage date and the UpdateDto does not carry it.
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] NewsEventCreateCommand command)
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

    // PUT /api/news-events/update -> full-object update keyed by id; Admin-only. 200 with the
    // affected id on success; PublishedAt is preserved (immutable). Not-found -> 400 failure.
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Update([FromBody] NewsEventUpdateCommand command)
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

    // DELETE /api/news-events/delete/{id} -> Admin-only. Cascades the crop/market link rows.
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Delete(Guid id)
    {
        // The acting admin comes from the JWT, never the route, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new NewsEventDeleteCommand(id, actingId.Value));
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
