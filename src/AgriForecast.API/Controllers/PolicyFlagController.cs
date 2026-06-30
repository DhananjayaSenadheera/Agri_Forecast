using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
using AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;
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

    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] PolicyFlagCreateCommand command)
    {
        var result = await mediator.Send(command);
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
