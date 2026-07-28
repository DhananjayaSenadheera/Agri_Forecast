namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// Why a dashboard leg is null. WIRE VALUES, lowercase snake_case, switched on by the farmer UI to pick a
/// sentence — never localised here.
/// <para>
/// These exist because the dashboard is DECORATION over the watchlist (PRD §3.7, fail-soft): a missing
/// price or a missing prediction must render as an honest "we don't have this yet", never as a fabricated
/// number and never as a failed request. Every null leg carries exactly one of these.
/// </para>
/// </summary>
public static class PortfolioUnavailableReasons
{
    /// <summary>
    /// This market has never published a usable price for this crop. Scoped to ONE market block: the
    /// farmer chose that market, so its tab reports its own emptiness rather than borrowing another
    /// market's number.
    /// <para>
    /// "Usable" is two things, and both are applied in the STORE so the query and the handler agree on
    /// which rows exist: the fail-closed hold filter (unit-confirmed, not quarantined), and carrying an
    /// actual quote in at least one price column. A commodity that was listed but not traded that day is
    /// a real row with no price, and it counts as no price here — it must not be able to stand in front of
    /// an older row that does have one.
    /// </para>
    /// <para>
    /// It is not a staleness signal: a price of any age is still a price and is served. This means the
    /// market has nothing at all for this crop.
    /// </para>
    /// </summary>
    public const string NoRecentPrice = "no_recent_price";

    /// <summary>
    /// No ForecastSnapshots row exists for this crop yet. The nightly pass writes one per crop per day, so
    /// this is normal for a crop added before the pass has ever covered it, and normal for every crop until
    /// the first pass runs. Never a reason to hide the crop or invent a price.
    /// </summary>
    public const string NoSnapshot = "no_snapshot";
}
