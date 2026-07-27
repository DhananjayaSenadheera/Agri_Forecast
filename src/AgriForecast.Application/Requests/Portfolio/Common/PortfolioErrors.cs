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

    /// <summary>Every code the endpoints can return as a 404.</summary>
    public static readonly IReadOnlyCollection<string> NotFoundCodes = new[]
    {
        WatchlistEntryNotFound
    };

    /// <summary>True when the error is a pinned not-found code (exact, case-sensitive match).</summary>
    public static bool IsNotFound(string? error) => error is not null && NotFoundCodes.Contains(error);
}
