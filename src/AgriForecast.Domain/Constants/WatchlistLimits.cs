namespace AgriForecast.Domain.Constants;

/// <summary>
/// The hard caps on a farmer's watchlist. Owner-decided product limits, not technical ones.
/// <para>
/// They live in Domain so the entity that enforces them and the handler that turns a breach into a wire
/// code read the SAME number — a cap duplicated across layers is a cap that drifts. They are deliberately
/// NOT database constraints: a check constraint could not be raised as
/// <c>watchlist_full</c> / <c>too_many_markets</c> without parsing a provider error, and the farmer needs
/// to be told which limit they hit, not handed a 500.
/// </para>
/// </summary>
public static class WatchlistLimits
{
    /// <summary>Crops one farmer may watch. The 11th add is refused with <c>watchlist_full</c>.</summary>
    public const int MaxCropsPerUser = 10;

    /// <summary>
    /// Markets one watched crop may follow. The 4th is refused with <c>too_many_markets</c>.
    /// Markets are a display/comparison choice only — predictions stay national, so this cap costs the
    /// farmer nothing in forecast quality.
    /// </summary>
    public const int MaxMarketsPerCrop = 3;

    /// <summary>
    /// The earliest planting date a farmer may record. Anything before this is a typo (a mis-keyed year),
    /// not a memory: the price history this product can reason about starts in 2015.
    /// </summary>
    public static readonly DateOnly EarliestPlantedDate = new(2000, 1, 1);
}
