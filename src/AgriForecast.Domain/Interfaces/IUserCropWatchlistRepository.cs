using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Write-side data access for the farmer watchlist. Every method is USER-SCOPED by signature — there is no
// "get by id" that could reach another farmer's row — so cross-user isolation cannot be forgotten at a
// call site. Reads for display go through IPortfolioReadStore (AsNoTracking); these loads are TRACKED
// because the caller mutates them and commits through IUnitofWorkRepository.
public interface IUserCropWatchlistRepository
{
    // Every row this user watches, tracked, oldest first, WITH its watched-market children loaded — the
    // caps and the full-replace diff are both computed against the children, so an absent collection would
    // read as "no markets" and silently re-insert rows that already exist.
    Task<List<UserCropWatchlist>> GetAllForUserAsync(Guid userId, CancellationToken ct = default);

    Task AddAsync(UserCropWatchlist entity, CancellationToken ct = default);

    void Remove(UserCropWatchlist entity);

    // Child-row persistence is explicit rather than left to EF's orphan handling: the handlers already
    // receive the exact insert/delete sets from the entity, and saying so at the repository keeps the
    // write set readable at the call site instead of implied by a navigation's delete behaviour.
    Task AddMarketsAsync(IEnumerable<UserCropWatchMarket> markets, CancellationToken ct = default);

    void RemoveMarkets(IEnumerable<UserCropWatchMarket> markets);
}
