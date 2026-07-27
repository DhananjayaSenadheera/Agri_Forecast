using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Write-side data access for the farmer watchlist. Every method is USER-SCOPED by signature — there is no
// "get by id" that could reach another farmer's row — so cross-user isolation cannot be forgotten at a
// call site. Reads for display go through IPortfolioReadStore (AsNoTracking); these loads are TRACKED
// because the caller mutates them and commits through IUnitofWorkRepository.
public interface IUserCropWatchlistRepository
{
    // Every row this user watches, tracked, oldest first. The home-market invariant needs the whole set in
    // one unit of work: setting a market rewrites all of them together.
    Task<List<UserCropWatchlist>> GetAllForUserAsync(Guid userId, CancellationToken ct = default);

    Task AddAsync(UserCropWatchlist entity, CancellationToken ct = default);

    void Remove(UserCropWatchlist entity);
}
