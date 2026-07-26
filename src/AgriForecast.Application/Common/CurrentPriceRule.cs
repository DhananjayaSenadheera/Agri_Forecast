using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Application.common;

// One definition of "today's price", shared by the harvest forecast and the best-harvest-window screens.
// Both put the number in front of the same farmer minutes apart, so they must never disagree.
public static class CurrentPriceRule
{
    // Trailing rows averaged, so one noisy market day cannot flip a verdict. Not a per-caller choice.
    public const int TrailingRows = 14;

    // Average daily mid (Min+Max)/2 over the newest TrailingRows rows with PriceDate <= asOf. The asOf
    // bound prevents lookahead: a historical decision date must never see prices observed after it.
    // Returns 0 when the crop has no recent prices — that means "unknown", and callers must surface it
    // as such rather than inventing a price.
    public static async Task<(decimal CurrentPrice, DateOnly? LatestObservation)> ComputeAsync(
        IMarketPriceRepository marketPriceRepository,
        Guid cropId,
        DateOnly asOf,
        CancellationToken ct = default)
    {
        var recent = await marketPriceRepository.GetRecentByCropIdAsync(
            cropId, TrailingRows, asOf, ct);

        if (recent.Count == 0) return (0m, null);

        return (Math.Round(recent.Average(p => (p.MinPrice + p.MaxPrice) / 2m), 2),
                recent.Max(p => p.PriceDate));
    }
}
