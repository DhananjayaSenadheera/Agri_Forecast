using AgriForecast.Application.Requests.Market.Commands.Create;
using AgriForecast.Application.Requests.Market.Quaries.GetAll;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// R2 D-DF3 — the registration path that replaces the retired EconomicCenters CRUD controller.
// "Register a new economic centre" = POST create with IsEconomicCenter = true. Mirrors
// CropController conventions (structured ToErrorResponse, [Authorize], BadRequest on failure —
// no stack traces leaked).
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

    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] MarketCreateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // Market registry read (API-1). Returns ALL markets by default (the admin registry
    // needs the full set of 12). ?hasPrices=true filters to markets carrying >=1 confirmed
    // PriceObservation (the farmer Prices page's price-carrying subset — 10 today). An
    // empty registry returns a 200 [] (never a 404).
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll([FromQuery] bool hasPrices = false)
    {
        var result = await mediator.Send(new GetMarketsQuery { HasPrices = hasPrices });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
