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

    // GET /api/news-events/get/all -> all events, newest knowledge date first. Stays [Authorize]
    // (authenticated-only, NOT Admin-gated): non-personal reference data, same posture as
    // PolicyFlag / FestivalCalendar reads. Returns 200 [] on empty — deliberately NOT the legacy
    // policy-flag 400-on-empty quirk.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new NewsEventGetAllQuery());
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // News events are curated data mutated by admins only (API-9). CAPTURE + STORAGE ONLY: unlike
    // PolicyFlag / FestivalCalendar these are NOT yet ML feature inputs (the model learns event
    // weights in a later, separate task), so there is deliberately NO training-data warning on the
    // mutations. PublishedAt is the immutable knowledge/vintage date — the UpdateDto does not carry
    // it, so it cannot be rewritten.
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] NewsEventCreateCommand command)
    {
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
        var result = await mediator.Send(new NewsEventDeleteCommand(id));
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
