using AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Read-only feed of the articles the news INGESTION pipeline captured (the Python-owned NewsArticles table
// behind the ML sentiment features). Distinct from /api/news-events, the admin-curated structured events
// CRUD — the two news stores never mix. Display-only: the pipeline is the only writer.
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

    // GET /api/news-articles/get/latest -> newest articles first. Plain [Authorize], not Admin-gated:
    // non-personal reference data. take is clamped by the handler (default 50, max 200) and never 400s, and
    // an empty or absent capture table returns 200 [].
    [HttpGet("get/latest")]
    public async Task<IActionResult> GetLatest([FromQuery] int? take)
    {
        var result = await mediator.Send(new NewsArticleGetLatestQuery { Take = take });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
