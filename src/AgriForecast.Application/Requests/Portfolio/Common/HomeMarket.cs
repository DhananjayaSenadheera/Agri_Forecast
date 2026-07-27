namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// The ONE rule for reading a farmer's home market back out of their watchlist rows.
/// <para>
/// The product law is one home market per farmer, but the column lives per row (reserved design space for
/// a future per-crop override). The application layer keeps every row in step: a write that sets the
/// market rewrites all of the user's rows in one transaction, and a newly added crop inherits the value.
/// So in practice the rows always agree and this returns the single distinct value.
/// </para>
/// <para>
/// It is written to be TOTAL anyway — a crash between two writes, or a future per-crop override, must not
/// make the dashboard undefined. Newest write wins, with the crop id as a stable tiebreak, so the answer
/// is deterministic rather than "whatever the database returned first".
/// </para>
/// </summary>
public static class HomeMarket
{
    /// <summary>
    /// The user's home market id, or null when they have no rows or have deliberately chosen no market
    /// (null = the national / economic-centre default, not missing data).
    /// </summary>
    public static Guid? Resolve(IEnumerable<HomeMarketCandidate> rows)
    {
        return rows
            .OrderByDescending(r => r.UpdatedAtUtc)
            .ThenBy(r => r.CropId)
            .Select(r => r.PreferredMarketId)
            .FirstOrDefault();
    }
}

/// <summary>One watchlist row reduced to just what the home-market rule looks at.</summary>
public readonly record struct HomeMarketCandidate(Guid CropId, Guid? PreferredMarketId, DateTime UpdatedAtUtc);
