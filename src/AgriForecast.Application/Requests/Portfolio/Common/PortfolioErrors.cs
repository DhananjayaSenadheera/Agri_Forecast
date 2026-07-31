namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// The frozen error codes the portfolio endpoints return. These are WIRE VALUES, not prose: the farmer UI
/// switches on them to choose its message, so they are lowercase snake_case, stable, and never localised
/// or reworded here. Precedent: IngestionServiceControlErrors.
/// <para>
/// The controller maps <see cref="WatchlistEntryNotFound"/> to HTTP 404 and the
/// <see cref="UnprocessableCodes"/> to 422, both with the body <c>{ "error": code }</c>. The
/// <see cref="BadRequestCodes"/> are 400 with that SAME code body; every other failure is a 400 in the
/// usual prose <c>{ errors: [{ property, message }] }</c> shape.
/// </para>
/// </summary>
public static class PortfolioErrors
{
    /// <summary>
    /// The caller has no watchlist row for that crop. Returned identically whether the row does not exist
    /// at all or belongs to ANOTHER farmer — 404, never 403. A 403 would confirm that someone else watches
    /// that crop, which is exactly the fact the owner scoping exists to keep private.
    /// </summary>
    public const string WatchlistEntryNotFound = "watchlist_entry_not_found";

    /// <summary>
    /// The caller already watches <see cref="Domain.Constants.WatchlistLimits.MaxCropsPerUser"/> crops.
    /// A cap, not a malformed request — the payload is perfectly valid, the account simply has no room —
    /// so it is a 422, not a 400.
    /// </summary>
    public const string WatchlistFull = "watchlist_full";

    /// <summary>
    /// The request asks one crop to follow more than
    /// <see cref="Domain.Constants.WatchlistLimits.MaxMarketsPerCrop"/> markets. Counted AFTER duplicate
    /// market ids are collapsed, so sending the same market twice is not what trips this.
    /// </summary>
    public const string TooManyMarkets = "too_many_markets";

    /// <summary>
    /// The planting date is in the future or before
    /// <see cref="Domain.Constants.WatchlistLimits.EarliestPlantedDate"/>. A farmer cannot have planted
    /// tomorrow, and a pre-2000 date is a mis-keyed year rather than a memory.
    /// </summary>
    public const string InvalidPlantedDate = "invalid_planted_date";

    // Clearing a recorded planting date requires a reason. These five are 400s, not 422s: unlike the caps
    // above, the payload itself is wrong — a reason is missing, contradicts the rest of the request, is
    // misspelled, or carries a note the request has no reason to attach it to. The UI's response is to fix
    // the payload (show the reason picker again), which is exactly what 400 means.

    /// <summary>
    /// The request clears a planting date the entry actually HAS, and carried no <c>clearReason</c>. A date
    /// disappearing with no recorded reason is the one outcome this feature exists to prevent.
    /// </summary>
    public const string ClearReasonRequired = "clear_reason_required";

    /// <summary>
    /// A <c>clearReason</c> was sent by a request that is NOT clearing an existing date — it sets a date, or
    /// clears one that was already null. Accepted-and-ignored would be a contract lie: the caller would
    /// believe a reason had been recorded when no removal happened at all.
    /// </summary>
    public const string ClearReasonNotApplicable = "clear_reason_not_applicable";

    /// <summary>
    /// <c>clearReason</c> is not one of <see cref="PlantedDateRemovalReasons.KnownReasons"/>. Matched
    /// case-sensitively, so <c>"cropfailed"</c> lands here rather than being guessed at.
    /// </summary>
    public const string InvalidClearReason = "invalid_clear_reason";

    /// <summary>
    /// A <c>clearReasonNote</c> arrived without a <c>clearReason</c>. The note annotates the reason; on its
    /// own it would be stored against nothing, or dropped silently.
    /// </summary>
    public const string ClearReasonNoteWithoutReason = "clear_reason_note_without_reason";

    /// <summary>
    /// <c>clearReasonNote</c> is longer than <see cref="Domain.Entities.PlantedDateRemoval.NoteMaxLength"/>
    /// characters. Rejected rather than truncated — silently shortening a farmer's own words is worse than
    /// asking them to shorten them.
    /// </summary>
    public const string ClearReasonNoteTooLong = "clear_reason_note_too_long";

    // The SALES LOG codes. A third pinned family on this controller, and the reasoning is the same as the
    // clear-reason one: every value below is a malformed payload (400) that the UI must react to
    // DIFFERENTLY — highlight the price box, highlight the date box, tell the farmer their note is too long
    // — so each gets a machine-readable code rather than a sentence to parse. The one exception is
    // SaleNotFound, which is a 404.
    //
    // ALL OF THEM ARE ANSWERED BEFORE ANY MUTATION. A request that fails halfway must leave the sales log
    // exactly as it was.

    /// <summary>
    /// No sale with that id belongs to the caller. Returned identically whether the row does not exist at
    /// all or belongs to ANOTHER farmer — 404, never 403, exactly like
    /// <see cref="WatchlistEntryNotFound"/>. A farmer's sales are the most private data in the product; a
    /// 403 would confirm that a given id is somebody's sale.
    /// </summary>
    public const string SaleNotFound = "sale_not_found";

    /// <summary>
    /// <c>pricePerKg</c> is missing or not greater than zero. A sale at Rs 0 is not a sale, and a missing
    /// price is the one field the row cannot be built without.
    /// </summary>
    /// <remarks>
    /// A price that is not a JSON number at all (<c>"abc"</c>) never reaches the handler: the input
    /// formatter refuses the body first and ASP.NET answers with its own 400. That is honest — it is a
    /// malformed request, not a mis-keyed price — and it is why this code covers missing and non-positive
    /// values rather than pretending to own JSON syntax.
    /// </remarks>
    public const string InvalidPrice = "invalid_price";

    /// <summary>
    /// <c>pricePerKg</c> is above <see cref="Domain.Constants.SaleLimits.MaxPricePerKg"/>. Told apart from
    /// <see cref="InvalidPrice"/> on purpose: "that cannot be a price" and "that is far too large" are
    /// different mistakes and the farmer fixes them differently (a typo'd zero versus a whole-lot total
    /// typed into a per-kilo box).
    /// </summary>
    public const string PriceOutOfRange = "price_out_of_range";

    /// <summary>
    /// <c>saleDate</c> is missing, blank, or not a <c>yyyy-MM-dd</c> date. The field is a STRING on the
    /// wire and parsed here, so every bad spelling lands on this one code instead of some of them becoming
    /// a serializer error the UI cannot switch on.
    /// </summary>
    public const string InvalidSaleDate = "invalid_sale_date";

    /// <summary>
    /// <c>saleDate</c> is after the caller's plausible local today. Nobody has sold tomorrow's harvest, and
    /// a future sale would sit at the top of the list forever.
    /// </summary>
    public const string SaleDateFuture = "sale_date_future";

    /// <summary>
    /// <c>quantityKg</c> was supplied but is not greater than zero, or is above
    /// <see cref="Domain.Constants.SaleLimits.MaxQuantityKg"/>. Quantity is OPTIONAL — omitting it is
    /// always fine — so this code only ever answers a value the farmer actually sent.
    /// </summary>
    public const string InvalidQuantity = "invalid_quantity";

    /// <summary>
    /// <c>note</c> is longer than <see cref="Domain.Entities.UserSale.NoteMaxLength"/> characters, MEASURED
    /// ON THE TRIMMED VALUE (which is what would be stored). Rejected rather than truncated, exactly like
    /// <see cref="ClearReasonNoteTooLong"/>: silently shortening a farmer's own words is worse than asking
    /// them to shorten them.
    /// </summary>
    public const string NoteTooLong = "note_too_long";

    /// <summary>
    /// <c>cropId</c> does not match an existing crop. A 400 rather than a 404, because the ROW being
    /// addressed (the sale) is not what is missing — a value inside the payload is.
    /// </summary>
    /// <remarks>
    /// SAME STATUS as the watchlist's unknown-crop answer, deliberately a CODED body rather than its prose
    /// shape. POST /api/portfolio/watchlist rejects an unknown crop from a FluentValidation rule, which can
    /// only produce <c>{ errors: [{ property, message }] }</c>; here the UI has to tell this apart from
    /// seven other 400s to know which field to highlight, so it gets a code it can switch on.
    /// </remarks>
    public const string UnknownCrop = "unknown_crop";

    /// <summary>
    /// <c>marketId</c> was supplied but does not match an existing market. Same 400 reasoning as
    /// <see cref="UnknownCrop"/>.
    /// </summary>
    public const string UnknownMarket = "unknown_market";

    /// <summary>Every code the endpoints can return as a 404.</summary>
    public static readonly IReadOnlyCollection<string> NotFoundCodes = new[]
    {
        WatchlistEntryNotFound,
        SaleNotFound
    };

    /// <summary>
    /// Every code the endpoints return as a 422. These are well-formed requests the product refuses, which
    /// is exactly what 422 means — a 400 would tell the UI the payload was malformed and send a developer
    /// looking for a serialization bug that is not there.
    /// </summary>
    public static readonly IReadOnlyCollection<string> UnprocessableCodes = new[]
    {
        WatchlistFull,
        TooManyMarkets,
        InvalidPlantedDate
    };

    /// <summary>
    /// Every code the endpoints return as a 400 WITH THE CODE BODY rather than the prose validation shape.
    /// These are malformed payloads, so 400 is right; they get the machine-readable body because the UI has
    /// to act on each one differently (re-prompt for a reason, flag the note, drop a stale field) and
    /// switching on a code beats parsing a sentence.
    /// </summary>
    public static readonly IReadOnlyCollection<string> BadRequestCodes = new[]
    {
        ClearReasonRequired,
        ClearReasonNotApplicable,
        InvalidClearReason,
        ClearReasonNoteWithoutReason,
        ClearReasonNoteTooLong,
        InvalidPrice,
        PriceOutOfRange,
        InvalidSaleDate,
        SaleDateFuture,
        InvalidQuantity,
        NoteTooLong,
        UnknownCrop,
        UnknownMarket
    };

    /// <summary>True when the error is a pinned not-found code (exact, case-sensitive match).</summary>
    public static bool IsNotFound(string? error) => error is not null && NotFoundCodes.Contains(error);

    /// <summary>True when the error is a pinned unprocessable code (exact, case-sensitive match).</summary>
    public static bool IsUnprocessable(string? error)
        => error is not null && UnprocessableCodes.Contains(error);

    /// <summary>True when the error is a pinned bad-request code (exact, case-sensitive match).</summary>
    public static bool IsBadRequestCode(string? error)
        => error is not null && BadRequestCodes.Contains(error);
}
