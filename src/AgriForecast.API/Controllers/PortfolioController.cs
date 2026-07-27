using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistMarket;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;
using AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

/// <summary>
/// The farmer's own portfolio: the crops they watch, their home market, and the dashboard built from them.
/// </summary>
/// <remarks>
/// Plain <c>[Authorize]</c>, NOT Admin — this is every farmer's personal surface, the opposite of the
/// admin-locked accuracy controller.
/// <para>
/// The caller's identity comes from the JWT subject on EVERY action and is never read from the body, the
/// route or the query string. That is the whole of the cross-user isolation story: there is no request
/// shape in which a farmer can name a different user, so the handlers only ever see their own id. A missing
/// or malformed subject claim is a 401, never a guess.
/// </para>
/// <para>
/// A crop the caller does not watch is a 404 on PUT and DELETE, whether the row does not exist at all or
/// belongs to another farmer. Never a 403: distinguishing the two would confirm that some other farmer
/// watches that crop.
/// </para>
/// </remarks>
[ApiController]
[Route("api/portfolio")]
[Authorize]
public class PortfolioController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Portfolio", message = error }
        }
    };

    // Machine-readable state code, a deliberately different shape from the validation-failure body, so the
    // UI can switch on it rather than parse prose. Mirrors the ingestion service-control 409 bodies.
    private static object ToCodeResponse(string code) => new { error = code };

    // GET /api/portfolio/watchlist — the caller's watched crops, ordered by crop name.
    // 200 [] for a farmer who has added nothing; the "add your crops" empty state is a UI concern.
    [HttpGet("watchlist")]
    public async Task<IActionResult> GetWatchlist()
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new GetWatchlistQuery { UserId = userId.Value });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/portfolio/watchlist { cropId, preferredMarketId? } — add a crop.
    // 200 { item, alreadyPresent } — IDEMPOTENT: re-adding a watched crop returns the existing row with
    //     alreadyPresent = true rather than a 409. A double-tap is not an error.
    // 400 — unknown cropId / unknown preferredMarketId (AddWatchlistCropCommandValidator).
    // An omitted or null preferredMarketId INHERITS the caller's current home market; it never clears it.
    [HttpPost("watchlist")]
    public async Task<IActionResult> AddToWatchlist([FromBody] AddWatchlistCropCommand command)
    {
        // The owner comes from the JWT, never the request body, so a farmer cannot write into someone
        // else's watchlist by editing the payload.
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.UserId = userId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/portfolio/watchlist/{cropId} { preferredMarketId? } — set the caller's home market.
    // 200 { cropId, preferredMarketId, preferredMarketName, appliedToCropCount } — applied to EVERY crop
    //     the caller watches, in one transaction (one home market per farmer).
    // 404 { "error": "watchlist_entry_not_found" } — the caller does not watch that crop.
    // A null preferredMarketId is meaningful here and clears the market back to the national default.
    [HttpPut("watchlist/{cropId}")]
    public async Task<IActionResult> UpdateWatchlistMarket(
        Guid cropId, [FromBody] UpdateWatchlistMarketCommand command)
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        command.UserId = userId.Value;
        // The route is the authority for which crop, so a mismatched body value cannot redirect the write.
        command.CropId = cropId;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        if (PortfolioErrors.IsNotFound(result.Error))
            return NotFound(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/portfolio/watchlist/{cropId} — remove a crop.
    // 200 { cropId, removed: true }
    // 404 { "error": "watchlist_entry_not_found" } — the caller does not watch that crop.
    [HttpDelete("watchlist/{cropId}")]
    public async Task<IActionResult> RemoveFromWatchlist(Guid cropId)
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new RemoveWatchlistCropCommand
        {
            UserId = userId.Value,
            CropId = cropId
        });
        if (result.IsSuccess)
            return Ok(result.Data);

        if (PortfolioErrors.IsNotFound(result.Error))
            return NotFound(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // GET /api/portfolio/dashboard — one item per watched crop: latest observed price + trend at the home
    // market (economic-centre fallback, flagged) plus the newest frozen forecast snapshot.
    // 200 with an empty items list for an empty watchlist. Both decorations are fail-soft: a missing leg is
    // null with a reason code, never a fabricated number and never a failed request.
    [HttpGet("dashboard")]
    public async Task<IActionResult> GetDashboard()
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new GetPortfolioDashboardQuery { UserId = userId.Value });
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
}
