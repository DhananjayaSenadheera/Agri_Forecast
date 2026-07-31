using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Write-side data access for a farmer's own sales log. Every method is USER-SCOPED BY SIGNATURE — there is
// no "get by id" that could reach another farmer's row — so cross-user isolation cannot be forgotten at a
// call site. Same posture as IUserCropWatchlistRepository, and for the same reason: the isolation story
// must not depend on every future handler remembering a WHERE clause.
//
// These loads are TRACKED, because the caller mutates them and commits through IUnitofWorkRepository.
// Reads for display go through IPortfolioReadStore (AsNoTracking).
public interface IUserSaleRepository
{
    // One sale belonging to THIS user, or null when the id is unknown OR belongs to somebody else. The
    // caller cannot tell those two apart, which is deliberate: both answer the same 404, because a 403
    // would confirm that the row exists for another farmer.
    Task<UserSale?> GetForUserAsync(Guid userId, Guid saleId, CancellationToken ct = default);

    Task AddAsync(UserSale sale, CancellationToken ct = default);

    void Remove(UserSale sale);
}
