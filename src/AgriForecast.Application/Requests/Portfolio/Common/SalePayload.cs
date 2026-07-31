using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// The validated, parsed body of a POST or PUT to <c>/api/portfolio/sales</c> — the shape the entity
/// factory can be handed without another thought.
/// </summary>
/// <remarks>
/// ONE VALIDATOR FOR BOTH VERBS. Create and update accept the same fields with the same rules, and two
/// hand-written copies of six checks is how a ceiling ends up enforced on POST and not on PUT.
/// <para>
/// It lives here rather than in a FluentValidation validator because every answer it gives is a PINNED WIRE
/// CODE the UI switches on (highlight the price box, highlight the date box), not prose — the same reason
/// the clear-reason contract is decided in its handler. It is also why the checks run BEFORE the row is
/// touched: a request that fails halfway must leave the sales log exactly as it was.
/// </para>
/// </remarks>
internal sealed record SalePayload(
    Guid? MarketId,
    DateOnly SaleDate,
    decimal PricePerKg,
    decimal? QuantityKg,
    string? Note)
{
    /// <summary>
    /// Validates and parses the wire fields. Returns the first failing code, or the parsed payload.
    /// </summary>
    /// <remarks>
    /// THE ORDER IS PART OF THE CONTRACT and is pinned by tests: price, then date, then quantity, then
    /// note. A request with two mistakes reports the first one in that order, every time — a UI that
    /// highlighted a different box on each retry would look broken.
    /// <para>
    /// The crop and market EXISTENCE checks are deliberately not here: they need the read store, so they
    /// run in the handler, after these (which are pure and free) have already passed.
    /// </para>
    /// </remarks>
    public static (string? Error, SalePayload? Value) Validate(
        Guid? marketId,
        string? saleDate,
        decimal? pricePerKg,
        decimal? quantityKg,
        string? note,
        DateTime nowUtc)
    {
        // Missing and non-positive share a code: both mean "that is not a price", and both are fixed in the
        // same box. Above the ceiling gets its OWN code, because a mis-keyed zero is a different mistake
        // from an empty field and the farmer fixes it differently.
        if (!pricePerKg.HasValue || pricePerKg.Value <= 0m)
            return (PortfolioErrors.InvalidPrice, null);

        if (pricePerKg.Value > SaleLimits.MaxPricePerKg)
            return (PortfolioErrors.PriceOutOfRange, null);

        // Strict yyyy-MM-dd: missing, blank and mis-spelled all land here rather than half of them becoming
        // a serializer error with a body the UI cannot switch on.
        var parsedDate = PortfolioTime.ParseYmd(saleDate);
        if (parsedDate is null)
            return (PortfolioErrors.InvalidSaleDate, null);

        // The SAME clock the planting-date rule reads, so the two surfaces cannot disagree about which day
        // it is. There is no floor: a farmer recalling a sale from years ago is recording history, which is
        // exactly what this log is for.
        if (parsedDate.Value > PortfolioTime.LatestPlausibleLocalDate(nowUtc))
            return (PortfolioErrors.SaleDateFuture, null);

        // Optional: an absent quantity is never an error. A supplied one must be a real amount.
        if (quantityKg.HasValue
            && (quantityKg.Value <= 0m || quantityKg.Value > SaleLimits.MaxQuantityKg))
            return (PortfolioErrors.InvalidQuantity, null);

        // MEASURED ON THE TRIMMED VALUE, which is what would be stored — so a note that is only over the cap
        // because of trailing whitespace is accepted and stored trimmed, and one that is genuinely too long
        // is REJECTED rather than silently shortened. Blank stores null: an empty string would read as "the
        // farmer wrote something" when they wrote nothing.
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is not null && trimmedNote.Length > UserSale.NoteMaxLength)
            return (PortfolioErrors.NoteTooLong, null);

        return (null, new SalePayload(
            marketId, parsedDate.Value, pricePerKg.Value, quantityKg, trimmedNote));
    }
}
