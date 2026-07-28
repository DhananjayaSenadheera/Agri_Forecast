using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
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
/// <para>
/// THREE ERROR SHAPES, deliberately distinct. A malformed request is a 400 with the usual
/// <c>{ errors: [{ property, message }] }</c> body. A row the caller does not own is a 404 with
/// <c>{ "error": "watchlist_entry_not_found" }</c>. A well-formed request the PRODUCT refuses — the 11th
/// crop, a 4th market, an impossible planting date — is a 422 with the same <c>{ "error": code }</c> body,
/// because telling the UI "bad request" for a limit the farmer hit would send a developer hunting a
/// serialization bug that does not exist.
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

    // GET /api/portfolio/watchlist — the caller's watched crops (each with its markets and planting
    // date), ordered by crop name.
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

    // POST /api/portfolio/watchlist { cropId, marketIds? } — add a crop with 0-3 markets.
    // 200 { item, alreadyPresent } — IDEMPOTENT: re-adding a watched crop returns the existing row with
    //     alreadyPresent = true rather than a 409. A double-tap is not an error. Markets sent on a repeat
    //     add are ADDED to the entry (insert-only), never replaced — replacing is what PUT is for.
    // 400 — unknown cropId / unknown marketId (AddWatchlistCropCommandValidator).
    // 422 { "error": "watchlist_full" } — the caller already watches the maximum number of crops.
    // 422 { "error": "too_many_markets" } — more than the per-crop market cap (counted after de-duping).
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

        if (PortfolioErrors.IsUnprocessable(result.Error))
            return UnprocessableEntity(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/portfolio/watchlist/{cropId} { marketIds?, plantedDate? } — update ONE watched crop.
    // 200 { item, marketsChanged, plantedDateChanged } — item is the full entry in GET's shape.
    // 404 { "error": "watchlist_entry_not_found" } — the caller does not watch that crop.
    // 422 { "error": "too_many_markets" | "invalid_planted_date" }.
    // marketIds present = FULL REPLACE ([] clears); omitted = unchanged. plantedDate null = clear,
    // omitted = unchanged. Per crop only — this never touches the caller's other crops.
    [HttpPut("watchlist/{cropId}")]
    public async Task<IActionResult> UpdateWatchlistEntry(
        Guid cropId, [FromBody] UpdateWatchlistEntryCommand command)
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

        if (PortfolioErrors.IsUnprocessable(result.Error))
            return UnprocessableEntity(ToCodeResponse(result.Error));

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

    // GET /api/portfolio/dashboard — one item per watched crop, carrying one price block per market that
    // crop is watched at (a crop with no chosen market gets a single economic-centre block flagged
    // isDefaultMarket) plus the crop's newest frozen forecast snapshot.
    // 200 with an empty items list for an empty watchlist. Every leg is fail-soft: a missing price or
    // prediction is null with a reason code, never a fabricated number and never a failed request. A
    // watched market with no data reports its own emptiness — it is never served another market's price.
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
