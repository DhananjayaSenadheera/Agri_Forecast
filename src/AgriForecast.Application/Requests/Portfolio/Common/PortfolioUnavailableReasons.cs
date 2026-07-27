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
    /// No usable price observation for this crop in the dashboard window, at the farmer's home market OR
    /// at the economic centre it falls back to. "Usable" is the fail-closed filter (unit-confirmed, not
    /// quarantined) the price endpoints already apply, so a held or unit-unproven row counts as no price.
    /// </summary>
    public const string NoRecentPrice = "no_recent_price";

    /// <summary>
    /// No ForecastSnapshots row exists for this crop yet. The nightly pass writes one per crop per day, so
    /// this is normal for a crop added before the pass has ever covered it, and normal for every crop until
    /// the first pass runs. Never a reason to hide the crop or invent a price.
    /// </summary>
    public const string NoSnapshot = "no_snapshot";
}
