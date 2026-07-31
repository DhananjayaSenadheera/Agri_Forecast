using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.DeleteSale;
using AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;
using AgriForecast.Application.Requests.Portfolio.Queries.GetSales;
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
/// THREE ERROR SHAPES, deliberately distinct. An unpinned failure is a 400 with the usual
/// <c>{ errors: [{ property, message }] }</c> body. A row the caller does not own is a 404 with
/// <c>{ "error": "watchlist_entry_not_found" }</c> or <c>{ "error": "sale_not_found" }</c>. A well-formed
/// request the PRODUCT refuses — the 11th crop, a 4th market, an impossible planting date — is a 422 with
/// the same <c>{ "error": code }</c> body, because telling the UI "bad request" for a limit the farmer hit
/// would send a developer hunting a serialization bug that does not exist.
/// </para>
/// <para>
/// TWO 400 FAMILIES carry the code body instead of the prose one, and both live in
/// <see cref="Application.Requests.Portfolio.Common.PortfolioErrors.BadRequestCodes"/>: the
/// <c>clear_reason_*</c> codes on PUT /watchlist/{cropId}, and the sales-log validation codes
/// (<c>invalid_price</c>, <c>price_out_of_range</c>, <c>invalid_sale_date</c>, <c>sale_date_future</c>,
/// <c>invalid_quantity</c>, <c>note_too_long</c>, <c>unknown_crop</c>, <c>unknown_market</c>). The status is
/// 400 in both because the payload really is wrong, but the UI must tell "you owe me a reason" apart from
/// "that note is too long", and "fix the price" apart from "fix the date" — so each needs a code and not a
/// sentence.
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

    // PUT /api/portfolio/watchlist/{cropId} { marketIds?, plantedDate?, clearReason?, clearReasonNote? } —
    // update ONE watched crop.
    // 200 { item, marketsChanged, plantedDateChanged } — item is the full entry in GET's shape.
    // 404 { "error": "watchlist_entry_not_found" } — the caller does not watch that crop.
    // 422 { "error": "too_many_markets" | "invalid_planted_date" }.
    // 400 { "error": "clear_reason_required" | "clear_reason_not_applicable" | "invalid_clear_reason"
    //                | "clear_reason_note_without_reason" | "clear_reason_note_too_long" } — the reason
    //     contract for clearing a recorded planting date. Same code body as the 404/422 above, because the
    //     UI has to react to each of these differently.
    // marketIds present = FULL REPLACE ([] clears); omitted = unchanged. plantedDate null = clear,
    // omitted = unchanged. Clearing a date the entry HAS requires clearReason (harvested | cropFailed |
    // enteredByMistake | other, case-sensitive) and refuses one otherwise.
    // Per crop only — this never touches the caller's other crops.
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

        // A malformed payload, but a PINNED one: the clear-reason codes are 400s that the UI switches on, so
        // they get the machine-readable body rather than the prose validation shape.
        if (PortfolioErrors.IsBadRequestCode(result.Error))
            return BadRequest(ToCodeResponse(result.Error));

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

    // THE SALES LOG (PRD 5.3). Everything below is the farmer's own self-reported sales — the most private
    // data in the product — and every action is scoped to the JWT subject exactly like the watchlist above.
    // A sale id the caller does not own is a 404 with { "error": "sale_not_found" }, identical to an id
    // that does not exist: a 403 would confirm that the id is somebody's sale.
    //
    // The validation failures are the pinned 400 code family (invalid_price, price_out_of_range,
    // invalid_sale_date, sale_date_future, invalid_quantity, note_too_long, unknown_crop, unknown_market),
    // all carrying the machine-readable { "error": code } body, because the UI has to highlight a different
    // field for each one.

    // GET /api/portfolio/sales?page=1&pageSize=20&cropId= — the caller's sales, newest first.
    // 200 { items, page, pageSize, total } — page/pageSize are echoed AS USED, i.e. after clamping
    //     (page >= 1, pageSize in [1, 50]). Out-of-range values are clamped rather than refused: a farmer
    //     scrolling on a phone must never be shown an error because a stale client asked for page 0.
    // cropId narrows the page to one crop (the More-details popup's list); an unknown crop id is an empty
    //     page, not an error.
    [HttpGet("sales")]
    public async Task<IActionResult> GetSales(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = GetSalesQuery.DefaultPageSize,
        [FromQuery] Guid? cropId = null)
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new GetSalesQuery
        {
            UserId = userId.Value,
            Page = page,
            PageSize = pageSize,
            CropId = cropId
        });

        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/portfolio/sales { cropId, marketId?, saleDate, pricePerKg, quantityKg?, note? }
    // 201 with the created sale (the same shape GET returns) and a Location header pointing at the list.
    // 400 { "error": <pinned code> } — see the family above.
    [HttpPost("sales")]
    public async Task<IActionResult> RecordSale([FromBody] RecordSaleCommand command)
    {
        // The owner comes from the JWT, never the request body, so a farmer cannot write a sale into
        // someone else's log by editing the payload.
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.UserId = userId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            // 201 Created, unlike the watchlist's idempotent 200: this really is a new row every time, and
            // two sales of the same crop on the same day are two facts, not a double-tap.
            // The Location header addresses the LIST, filtered to the row's crop: there is no
            // GET /sales/{id} route (the popup and the page both read lists), and inventing one just to
            // have somewhere to point would be a route nobody calls.
            return Created($"/api/portfolio/sales?cropId={result.Data.CropId}", result.Data);

        if (PortfolioErrors.IsBadRequestCode(result.Error))
            return BadRequest(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/portfolio/sales/{id} { marketId?, saleDate, pricePerKg, quantityKg?, note? }
    // 200 with the updated sale.
    // 404 { "error": "sale_not_found" } — the caller does not own that sale (or it does not exist).
    // 400 { "error": <pinned code> }.
    // A TRUE PUT: the body is the row's complete new state, so an ABSENT optional key CLEARS that value
    // (marketId, quantityKg, note). There is no cropId — a sale's crop is immutable, and a sale recorded
    // against the wrong crop is deleted and re-added rather than re-pointed.
    [HttpPut("sales/{id}")]
    public async Task<IActionResult> UpdateSale(Guid id, [FromBody] UpdateSaleCommand command)
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        command.UserId = userId.Value;
        // The route is the authority for which sale, so a mismatched body value cannot redirect the write.
        command.SaleId = id;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        if (PortfolioErrors.IsNotFound(result.Error))
            return NotFound(ToCodeResponse(result.Error));

        if (PortfolioErrors.IsBadRequestCode(result.Error))
            return BadRequest(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/portfolio/sales/{id}
    // 204 No Content — a hard delete of the farmer's own row; there is nothing left to describe.
    // 404 { "error": "sale_not_found" } — including the SECOND delete of the same id. Not idempotent on
    //     purpose: telling "you already deleted this" apart from "that was never yours" would reveal which
    //     ids exist, and privacy wins over tidiness.
    [HttpDelete("sales/{id}")]
    public async Task<IActionResult> DeleteSale(Guid id)
    {
        var userId = this.GetActingUserId();
        if (userId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new DeleteSaleCommand
        {
            UserId = userId.Value,
            SaleId = id
        });

        if (result.IsSuccess)
            return NoContent();

        if (PortfolioErrors.IsNotFound(result.Error))
            return NotFound(ToCodeResponse(result.Error));

        return BadRequest(ToErrorResponse(result.Error));
    }
}
