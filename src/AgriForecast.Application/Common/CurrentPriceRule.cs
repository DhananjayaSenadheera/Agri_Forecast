using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Application.common;

// "Today's price" as EVERY farmer-facing screen must agree it is defined.
//
// Two screens put this number in front of the same farmer minutes apart: the
// harvest forecast ("Not recommended: forecast below today's price") and the best
// harvest window ("plant here, expect Rs X"). If each derived its own current
// price they could disagree, and the app would recommend a window whose forecast
// the other screen calls a loss. One definition, one query, both callers.
public static class CurrentPriceRule
{
    // Trailing rows averaged for the current price (agri-ml-engineer: one noisy
    // market day must not be able to flip a verdict). Not a per-caller choice.
    public const int TrailingRows = 14;

    // Average daily mid (Min+Max)/2 over the newest TrailingRows rows with
    // PriceDate <= asOf. The asOf bound prevents lookahead leakage: a historical
    // decision date must never see prices observed after it.
    //
    // Returns 0 when the crop has no recent prices. That is "unknown" — callers
    // surface it as such; they must never invent a price nor turn it into an error.
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
