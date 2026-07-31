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
        // ROUNDED TO THE COLUMN'S SCALE BEFORE ANYTHING IS CHECKED, so the number that is validated is the
        // number that is stored, returned and audited — all four provably identical.
        //
        // A farmer who types Rs 155.999 gets Rs 156.00 rather than a rejection: it is obviously a price,
        // refusing it over a third decimal would be unkind, and the money columns are decimal(10,2) /
        // decimal(12,2) so SOMETHING was always going to round it. Doing it here rather than leaving it to
        // SQL Server is what stops the response (read back from the database) and the audit line (rendered
        // from this payload) drifting apart by a cent. AwayFromZero matches SQL Server's own scale
        // conversion, so a value that skips this path still lands on the same number.
        var price = Round(pricePerKg);
        var quantity = Round(quantityKg);

        // Missing and non-positive share a code: both mean "that is not a price", and both are fixed in the
        // same box. Above the ceiling gets its OWN code, because a mis-keyed zero is a different mistake
        // from an empty field and the farmer fixes it differently. Checked on the ROUNDED value, so a price
        // that rounds to zero is refused as the zero it would have been stored as.
        if (!price.HasValue || price.Value <= 0m)
            return (PortfolioErrors.InvalidPrice, null);

        if (price.Value > SaleLimits.MaxPricePerKg)
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

        // Optional: an absent quantity is never an error. A supplied one must be a real amount — again
        // measured on the rounded value, so a quantity that rounds to zero is refused rather than stored
        // as "0 kg".
        if (quantity.HasValue
            && (quantity.Value <= 0m || quantity.Value > SaleLimits.MaxQuantityKg))
            return (PortfolioErrors.InvalidQuantity, null);

        // MEASURED ON THE TRIMMED VALUE, which is what would be stored — so a note that is only over the cap
        // because of trailing whitespace is accepted and stored trimmed, and one that is genuinely too long
        // is REJECTED rather than silently shortened. Blank stores null: an empty string would read as "the
        // farmer wrote something" when they wrote nothing.
        var trimmedNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (trimmedNote is not null && trimmedNote.Length > UserSale.NoteMaxLength)
            return (PortfolioErrors.NoteTooLong, null);

        return (null, new SalePayload(
            marketId, parsedDate.Value, price.Value, quantity, trimmedNote));
    }

    /// <summary>
    /// The scale of the money columns (<c>decimal(10,2)</c> and <c>decimal(12,2)</c>) — cents on the price,
    /// 10 g on the quantity.
    /// </summary>
    public const int MoneyScale = 2;

    /// <summary>
    /// Rounds to <see cref="MoneyScale"/> decimal places, away from zero on a midpoint — the same rule SQL
    /// Server applies when narrowing a decimal's scale, so this never disagrees with the column.
    /// </summary>
    public static decimal? Round(decimal? value)
        => value.HasValue
            ? decimal.Round(value.Value, MoneyScale, MidpointRounding.AwayFromZero)
            : null;
}
