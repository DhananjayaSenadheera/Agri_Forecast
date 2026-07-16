using AgriForecast.Application.Requests.Prices.Quaries.GetHistory;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Observed market-price history (API-2). Distinct from ForecastController: this serves
// REAL past observations (a daily low-high envelope), never a forecast. Route matches the
// FE proposal in src/api/types.ts / fixtures.ts: GET /api/prices/crop/{cropId}/history.
// [Authorize] like the rest of the data surface.
[ApiController]
[Route("api/prices")]
[Authorize]
public class PricesController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Prices", message = error }
        }
    };

    // marketId optional (null => cross-market envelope for the crop). days is clamped to
    // [7, 365] in the handler (default 90). No matching data returns a 200 [] (never a 404).
    [HttpGet("crop/{cropId}/history")]
    public async Task<IActionResult> GetPriceHistory(
        Guid cropId, [FromQuery] Guid? marketId = null, [FromQuery] int days = 90)
    {
        var result = await mediator.Send(new GetPriceHistoryQuery
        {
            CropId = cropId,
            MarketId = marketId,
            Days = days
        });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
