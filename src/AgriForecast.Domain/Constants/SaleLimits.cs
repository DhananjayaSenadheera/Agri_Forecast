namespace AgriForecast.Domain.Constants;

/// <summary>
/// The sanity bounds on a farmer's self-reported sale. Owner-decided product limits, not technical ones.
/// <para>
/// They live in Domain for the same reason <see cref="WatchlistLimits"/> does: the entity that enforces
/// them and the handler that turns a breach into a wire code must read the SAME number, and a cap
/// duplicated across layers is a cap that drifts. They are deliberately NOT check constraints — a provider
/// error could not be raised as <c>price_out_of_range</c> without parsing its text, and the farmer needs to
/// be told which value they mis-keyed, not handed a 500.
/// </para>
/// </summary>
public static class SaleLimits
{
    /// <summary>
    /// The highest price per kilo a sale may claim, INCLUSIVE. Anything above it is a mis-keyed number (an
    /// extra zero, or a whole-lot total typed into a per-kilo box), not a price: the most expensive thing
    /// this market carries trades two orders of magnitude below it. Refused with
    /// <c>price_out_of_range</c>.
    /// </summary>
    public const decimal MaxPricePerKg = 100_000m;

    /// <summary>
    /// The largest quantity a single sale may claim, INCLUSIVE, in kilos. Same reasoning as the price cap:
    /// past this it is a typo rather than a harvest. Refused with <c>invalid_quantity</c>, which also covers
    /// a zero or negative amount — quantity is optional, but "0 kg" is not a sale.
    /// </summary>
    public const decimal MaxQuantityKg = 100_000m;
}
