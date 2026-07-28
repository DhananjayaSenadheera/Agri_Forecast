namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// The frozen error codes the portfolio endpoints return. These are WIRE VALUES, not prose: the farmer UI
/// switches on them to choose its message, so they are lowercase snake_case, stable, and never localised
/// or reworded here. Precedent: IngestionServiceControlErrors.
/// <para>
/// The controller maps <see cref="WatchlistEntryNotFound"/> to HTTP 404 with the body
/// <c>{ "error": "watchlist_entry_not_found" }</c>. Everything else is a 400 in the usual
/// <c>{ errors: [{ property, message }] }</c> shape.
/// </para>
/// </summary>
public static class PortfolioErrors
{
    /// <summary>
    /// The caller has no watchlist row for that crop. Returned identically whether the row does not exist
    /// at all or belongs to ANOTHER farmer — 404, never 403. A 403 would confirm that someone else watches
    /// that crop, which is exactly the fact the owner scoping exists to keep private.
    /// </summary>
    public const string WatchlistEntryNotFound = "watchlist_entry_not_found";

    /// <summary>
    /// The caller already watches <see cref="Domain.Constants.WatchlistLimits.MaxCropsPerUser"/> crops.
    /// A cap, not a malformed request — the payload is perfectly valid, the account simply has no room —
    /// so it is a 422, not a 400.
    /// </summary>
    public const string WatchlistFull = "watchlist_full";

    /// <summary>
    /// The request asks one crop to follow more than
    /// <see cref="Domain.Constants.WatchlistLimits.MaxMarketsPerCrop"/> markets. Counted AFTER duplicate
    /// market ids are collapsed, so sending the same market twice is not what trips this.
    /// </summary>
    public const string TooManyMarkets = "too_many_markets";

    /// <summary>
    /// The planting date is in the future or before
    /// <see cref="Domain.Constants.WatchlistLimits.EarliestPlantedDate"/>. A farmer cannot have planted
    /// tomorrow, and a pre-2000 date is a mis-keyed year rather than a memory.
    /// </summary>
    public const string InvalidPlantedDate = "invalid_planted_date";

    /// <summary>Every code the endpoints can return as a 404.</summary>
    public static readonly IReadOnlyCollection<string> NotFoundCodes = new[]
    {
        WatchlistEntryNotFound
    };

    /// <summary>
    /// Every code the endpoints return as a 422. These are well-formed requests the product refuses, which
    /// is exactly what 422 means — a 400 would tell the UI the payload was malformed and send a developer
    /// looking for a serialization bug that is not there.
    /// </summary>
    public static readonly IReadOnlyCollection<string> UnprocessableCodes = new[]
    {
        WatchlistFull,
        TooManyMarkets,
        InvalidPlantedDate
    };

    /// <summary>True when the error is a pinned not-found code (exact, case-sensitive match).</summary>
    public static bool IsNotFound(string? error) => error is not null && NotFoundCodes.Contains(error);

    /// <summary>True when the error is a pinned unprocessable code (exact, case-sensitive match).</summary>
    public static bool IsUnprocessable(string? error)
        => error is not null && UnprocessableCodes.Contains(error);
}
