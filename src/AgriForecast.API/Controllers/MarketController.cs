using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Market.Commands.Create;
using AgriForecast.Application.Requests.Market.Quaries.GetAll;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// The registration path that replaced the retired EconomicCenters CRUD controller: registering a new
// economic centre is a create with IsEconomicCenter = true. Mirrors CropController conventions.
[ApiController]
[Route("api/markets")]
[Authorize]
public class MarketController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Market", message = error }
        }
    };

    // Registering a market / economic centre mutates the market dimension — Admin-only (API-9).
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Create([FromBody] MarketCreateCommand command)
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

    // Market registry read. Returns ALL markets by default, since the admin registry needs the full set;
    // ?hasPrices=true filters to markets carrying at least one confirmed PriceObservation. An empty registry
    // returns a 200 [], never a 404.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll([FromQuery] bool hasPrices = false)
    {
        var result = await mediator.Send(new GetMarketsQuery { HasPrices = hasPrices });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
