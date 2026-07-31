using AgriForecast.Domain.Constants;

namespace AgriForecast.Domain.Entities;

// One sale a farmer typed in themselves (table UserSales): "on this day I sold this crop at this price".
// A lightweight self-report — no invoice, no document, no proof — and the farmer's own record, editable and
// deletable by them and by nobody else.
//
// QUARANTINED (PRD 3.1). This table is the one place farmer-entered prices live and it is a DEAD END for
// them: nothing copies a row into PriceObservations or MarketPrices, no view or computed column exposes
// PricePerKg to the feature layer, no navigation reaches here from Users, Crops or Markets, and the Python
// loader is statically forbidden from so much as naming the table. A farmer's own price training the model
// that then advises that farmer is a feedback loop dressed up as data, so the quarantine is a law and not a
// preference.
//
// CROPID IS IMMUTABLE. There is no method here that takes one, which is the domain half of "a sale
// recorded against the wrong crop is deleted and re-added, never re-pointed": a row that changed crops
// would silently re-attribute a price the farmer reported about something else.
//
// Privacy: UserId/CropId/MarketId are ids and Note is the farmer's own short free text. The note lives HERE
// and nowhere else — never in UserActivityLog.Details, which is code-authored text only (see
// SaleAuditDetails, whose signature cannot accept it). Times are passed in so tests are deterministic.
// Style precedent: UserCropWatchlist (mutable, factory-built) and PlantedDateRemoval (guards every input).
public class UserSale
{
    public Guid Id { get; private set; }

    // FK -> Users (Cascade): personal data that does not outlive its owner. Deleting an account takes the
    // farmer's own sales with it, exactly like their watchlist and their planting-date removals.
    public Guid UserId { get; private set; }

    // FK -> Crops (Restrict): reference data a farmer's record refers to cannot be deleted out from under
    // it. A crop delete fails loudly rather than shredding somebody's sales history.
    public Guid CropId { get; private set; }

    // FK -> Markets (Restrict), OPTIONAL: where the sale happened, when the farmer bothered to say. Null is
    // a normal state ("I sold it, I am not telling you where"), not missing data — the three-way comparison
    // simply falls back to the national series for that row.
    public Guid? MarketId { get; private set; }

    // The day of the sale. Date-only, no hidden time component: a sale day that carried 00:00:00 would be
    // "the day before" for half the world.
    public DateOnly SaleDate { get; private set; }

    // What the farmer got, per kilo, in LKR. decimal(10,2) — money, never a float.
    public decimal PricePerKg { get; private set; }

    // How much they sold, in kilos. decimal(12,2) and OPTIONAL: plenty of farmers remember the price and
    // not the weight, and demanding both would cost us the price too.
    public decimal? QuantityKg { get; private set; }

    // The farmer's optional note. Trimmed; blank stores null rather than an empty string.
    public string? Note { get; private set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// nvarchar column cap for <see cref="Note"/>, mirrored by the EF configuration and by the wire
    /// validation, which REJECTS an over-long note rather than letting it reach the truncation below.
    /// Precedent: <see cref="PlantedDateRemoval.NoteMaxLength"/>.
    /// </summary>
    public const int NoteMaxLength = 500;

    private UserSale() { }

    /// <summary>
    /// Records one sale. Every input is guarded here, so a row that reached the table is a row that means
    /// something — the wire codes in the application layer are the FIRST answer, and these throws are the
    /// last.
    /// </summary>
    public static UserSale Record(
        Guid userId,
        Guid cropId,
        Guid? marketId,
        DateOnly saleDate,
        decimal pricePerKg,
        decimal? quantityKg,
        string? note,
        DateTime createdAtUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId is required.", nameof(cropId));

        // An all-zeros market is not "no market" — it is a caller who lost a real id somewhere. Omitting
        // the market is spelled null, and the two must not be the same thing.
        GuardMarketId(marketId, nameof(marketId));
        GuardSaleDate(saleDate, nameof(saleDate));
        GuardPrice(pricePerKg, nameof(pricePerKg));
        GuardQuantity(quantityKg, nameof(quantityKg));

        if (createdAtUtc == default)
            throw new ArgumentException("CreatedAtUtc is required.", nameof(createdAtUtc));

        return new UserSale
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CropId = cropId,
            MarketId = marketId,
            SaleDate = saleDate,
            PricePerKg = pricePerKg,
            QuantityKg = quantityKg,
            Note = Cap(note, NoteMaxLength),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Applies an edit to every mutable field at once. Returns true when something actually changed, so a
    /// no-op PUT does not churn <see cref="UpdatedAtUtc"/>.
    /// </summary>
    /// <remarks>
    /// FULL REPLACE of the mutable fields, which is what PUT means: a null <paramref name="marketId"/>,
    /// <paramref name="quantityKg"/> or <paramref name="note"/> CLEARS that value. The caller decides
    /// whether an absent JSON key means "clear" or "leave alone" — for this endpoint it means clear, and
    /// the tri-state trickery of PUT /watchlist/{cropId} is deliberately not repeated here.
    /// <para>
    /// THE CROP IS NOT A PARAMETER, and that is the enforcement. A sale recorded against the wrong crop is
    /// deleted and re-added; re-pointing one would silently re-attribute a reported price to a crop the
    /// farmer never said it was about, and a method that cannot take a crop id cannot do that by mistake.
    /// </para>
    /// </remarks>
    public bool Revise(
        Guid? marketId,
        DateOnly saleDate,
        decimal pricePerKg,
        decimal? quantityKg,
        string? note,
        DateTime updatedAtUtc)
    {
        GuardMarketId(marketId, nameof(marketId));
        GuardSaleDate(saleDate, nameof(saleDate));
        GuardPrice(pricePerKg, nameof(pricePerKg));
        GuardQuantity(quantityKg, nameof(quantityKg));

        if (updatedAtUtc == default)
            throw new ArgumentException("UpdatedAtUtc is required.", nameof(updatedAtUtc));

        var capped = Cap(note, NoteMaxLength);

        var changed = MarketId != marketId
                      || SaleDate != saleDate
                      || PricePerKg != pricePerKg
                      || QuantityKg != quantityKg
                      || Note != capped;

        if (!changed)
            return false;

        MarketId = marketId;
        SaleDate = saleDate;
        PricePerKg = pricePerKg;
        QuantityKg = quantityKg;
        Note = capped;
        UpdatedAtUtc = updatedAtUtc;
        return true;
    }

    private static void GuardMarketId(Guid? marketId, string parameterName)
    {
        if (marketId.HasValue && marketId.Value == Guid.Empty)
            throw new ArgumentException(
                "MarketId must be a real market id; omit it (null) to record no market.", parameterName);
    }

    // 0001-01-01 is what an unset DateOnly looks like. A sale with no plausible day cannot be compared to
    // anything, which is the only reason the row exists.
    private static void GuardSaleDate(DateOnly saleDate, string parameterName)
    {
        if (saleDate == default)
            throw new ArgumentException("SaleDate is required.", parameterName);
    }

    // The future half of the date rule needs a clock and therefore lives in the application layer (see
    // PortfolioTime), exactly as UserCropWatchlist.SetPlantedDate leaves "not in the future" to its handler:
    // an entity that read DateTime.UtcNow would be untestable and silently timezone-dependent.

    private static void GuardPrice(decimal pricePerKg, string parameterName)
    {
        if (pricePerKg <= 0m)
            throw new ArgumentException("PricePerKg must be greater than zero.", parameterName);

        if (pricePerKg > SaleLimits.MaxPricePerKg)
            throw new ArgumentException(
                $"PricePerKg must be at most {SaleLimits.MaxPricePerKg}.", parameterName);
    }

    private static void GuardQuantity(decimal? quantityKg, string parameterName)
    {
        if (!quantityKg.HasValue)
            return;

        if (quantityKg.Value <= 0m)
            throw new ArgumentException(
                "QuantityKg must be greater than zero when supplied; omit it (null) instead.",
                parameterName);

        if (quantityKg.Value > SaleLimits.MaxQuantityKg)
            throw new ArgumentException(
                $"QuantityKg must be at most {SaleLimits.MaxQuantityKg}.", parameterName);
    }

    // Trim then cap to the column length; a blank value stores null rather than an empty string. The
    // truncation is defence in depth only: the application layer answers an over-long note with the
    // note_too_long wire code instead of silently shortening a farmer's own words.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
