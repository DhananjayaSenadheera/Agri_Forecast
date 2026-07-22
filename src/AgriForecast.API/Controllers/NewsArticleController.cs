using AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Read-only feed of the articles the news INGESTION pipeline captured (the Python-owned
// NewsArticles table — the raw material behind the ML sentiment features). Distinct from
// /api/news-events, the admin-CURATED structured events CRUD: the two "news" stores never mix.
// This controller is display-only — no mutations; the pipeline is the only writer.
[ApiController]
[Route("api/news-articles")]
[Authorize]
public class NewsArticleController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "News Article", message = error }
        }
    };

    // GET /api/news-articles/get/latest?take=50 -> newest articles first. Stays [Authorize]
    // (authenticated-only, NOT Admin-gated): non-personal reference data, same posture as the
    // news-events read. take is clamped by the handler (default 50, max 200) — never a 400.
    // Returns 200 [] when the capture table is empty or absent (ingestion never ran).
    [HttpGet("get/latest")]
    public async Task<IActionResult> GetLatest([FromQuery] int? take)
    {
        var result = await mediator.Send(new NewsArticleGetLatestQuery { Take = take });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
