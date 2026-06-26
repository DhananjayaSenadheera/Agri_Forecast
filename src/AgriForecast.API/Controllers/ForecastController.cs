using AgriForecast.Application.Requests.Crop.Quaries.GetBest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetMonthly;
using AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/forecast")]
public class ForecastController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Forecast", message = error }
        }
    };

    [HttpGet("monthly/{cropId}")]
    public async Task<IActionResult> GetMonthlyForecast(Guid cropId, [FromQuery] int months = 12)
    {
        var result = await mediator.Send(new GetMonthlyForecastQuery { CropId = cropId, Months = months });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpGet("crop/{cropId}/harvest")]
    public async Task<IActionResult> GetHarvestForecast(Guid cropId, [FromQuery] DateOnly plantDate)
    {
        var result = await mediator.Send(new GetHarvestForecastQuery { CropId = cropId, PlantDate = plantDate });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpGet("crop/{cropId}/timeline")]
    public async Task<IActionResult> GetCropTimeline(Guid cropId, [FromQuery] int months = 12, [FromQuery] DateOnly? asOf = null)
    {
        var result = await mediator.Send(new GetCropTimelineQuery { CropId = cropId, Months = months, AsOf = asOf });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpGet("best-crops")]
    public async Task<IActionResult> GetBestCrops([FromQuery] int lookbackMonths = 3)
    {
        var result = await mediator.Send(new GetBestCropsQuery { LookbackMonths = lookbackMonths });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
