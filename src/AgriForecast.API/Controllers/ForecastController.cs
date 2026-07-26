using AgriForecast.Application.Requests.Crop.Quaries.GetBest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetCropReadiness;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;
using AgriForecast.Application.Requests.Forecast.Quaries.GetMarketOverview;
using AgriForecast.Application.Requests.Forecast.Quaries.GetMonthly;
using AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/forecast")]
[Authorize]
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

    // Best planting/harvest window: ranks candidate planting dates by the price their harvest is forecast to
    // fetch. A not-rankable response (rankable=false plus a reasonCode) is a valid 200; only an ML transport
    // failure returns a 400.
    [HttpGet("crop/{cropId}/harvest-window")]
    public async Task<IActionResult> GetHarvestWindow(Guid cropId, [FromQuery] int horizonDays = 90, [FromQuery] DateOnly? asOf = null)
    {
        var result = await mediator.Send(new GetHarvestWindowQuery { CropId = cropId, HorizonDays = horizonDays, AsOf = asOf });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // Per-crop forecast-readiness map: a read-only passthrough of the promoted model payload's serving
    // decision, recomputed by every train run. The empty map (modelActive=false) is a valid 200; only an ML
    // transport failure returns the 400 error shape.
    [HttpGet("crop-readiness")]
    public async Task<IActionResult> GetCropReadiness()
    {
        var result = await mediator.Send(new GetCropReadinessQuery());
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

    // Read-only market snapshot for the farmer-app landing screen. days is clamped to [7, 90] in the handler
    // (default 30). Empty data returns a 200 with empty arrays and a null asOf, never a 404.
    [HttpGet("market-overview")]
    public async Task<IActionResult> GetMarketOverview([FromQuery] int days = 30)
    {
        var result = await mediator.Send(new GetMarketOverviewQuery { Days = days });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
