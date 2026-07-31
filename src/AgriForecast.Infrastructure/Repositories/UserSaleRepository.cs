using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

// Write-side access to UserSales. TRACKED loads (no AsNoTracking) — the handlers mutate or delete what they
// load and commit through IUnitofWorkRepository, so one SaveChanges is the whole write.
//
// THE USER FILTER IS BAKED INTO THE QUERY, not left to the caller: there is deliberately no by-id load, so
// no handler can reach a row belonging to a different farmer even by mistake. Same posture as
// UserCropWatchlistRepository, and it matters more here — these rows are the farmer's own sale prices.
public class UserSaleRepository : IUserSaleRepository
{
    private readonly AgriForecastDbContext _db;

    public UserSaleRepository(AgriForecastDbContext db) => _db = db;

    // Both predicates in ONE WHERE: an id that exists under another user returns null exactly like an id
    // that exists nowhere, which is what lets the handler answer a single 404 for both.
    public async Task<UserSale?> GetForUserAsync(
        Guid userId, Guid saleId, CancellationToken ct = default)
        => await _db.UserSales
            .FirstOrDefaultAsync(s => s.UserId == userId && s.Id == saleId, ct);

    public async Task AddAsync(UserSale sale, CancellationToken ct = default)
        => await _db.UserSales.AddAsync(sale, ct);

    public void Remove(UserSale sale) => _db.UserSales.Remove(sale);
}
