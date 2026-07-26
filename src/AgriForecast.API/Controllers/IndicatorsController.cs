using AgriForecast.Application.Requests.Indicators.Quaries.GetIndicatorSeries;
using AgriForecast.Application.Requests.Indicators.Quaries.GetMacroSeries;
using AgriForecast.Application.Requests.Indicators.Quaries.GetSeriesCatalog;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Read-only economic-indicator and macro-series browser backing the admin Indicators page.
//
// Plain [Authorize], not Admin-only: these reads are non-personal national reference data, no more
// sensitive than /api/forecast/market-overview or /api/markets, and the farmer app may surface an
// indicators view later. This data is only ever mutated by ingestion, never here.
//
// Routes use full literal paths so this one controller serves /api/indicators, /api/macro-series and
// /api/indicators/catalog. Empty results are a 200 []; from > to is a 400 with the house error shape.
[ApiController]
[Route("api")]
[Authorize]
public class IndicatorsController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Indicators", message = error }
        }
    };

    // GET /api/indicators?code=USD_LKR&from=2025-07-01&to=2026-07-01
    // Daily EconomicIndicators readings for one code. from/to inclusive + optional (default:
    // last 365 days ending at the series' latest date). Unknown/empty series -> 200 [].
    [HttpGet("indicators")]
    public async Task<IActionResult> GetIndicators(
        [FromQuery] string? code = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null)
    {
        var result = await mediator.Send(new GetIndicatorSeriesQuery { Code = code, From = from, To = to });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/macro-series?key=CCPI_BASE2021&from=2025-07-01&to=2026-07-01
    // Vintage-aware MacroSeriesPoints for one key. BOTH referenceDate and publishedAt are
    // returned verbatim (never collapsed). Window filters referenceDate only. Empty -> 200 [].
    [HttpGet("macro-series")]
    public async Task<IActionResult> GetMacroSeries(
        [FromQuery] string? key = null,
        [FromQuery] DateOnly? from = null,
        [FromQuery] DateOnly? to = null)
    {
        var result = await mediator.Send(new GetMacroSeriesQuery { Key = key, From = from, To = to });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/indicators/catalog
    // Distinct series across both tables, each { key, kind: "indicator"|"macro", latestDate,
    // count }. Feeds the series picker (which currently hardcodes CCPI_BASE2021).
    [HttpGet("indicators/catalog")]
    public async Task<IActionResult> GetCatalog()
    {
        var result = await mediator.Send(new GetSeriesCatalogQuery());
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
