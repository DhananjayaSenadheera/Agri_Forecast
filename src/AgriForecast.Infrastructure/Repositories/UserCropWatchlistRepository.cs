using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

// Write-side access to UserCropWatchlist. TRACKED loads (no AsNoTracking) — the handlers mutate what they
// load and commit through IUnitofWorkRepository, so one SaveChanges writes the whole user-wide home-market
// change as a single transaction.
//
// The user filter is baked into the query, not left to the caller: there is deliberately no by-id load, so
// no handler can accidentally reach a row belonging to a different farmer.
public class UserCropWatchlistRepository : IUserCropWatchlistRepository
{
    private readonly AgriForecastDbContext _db;

    public UserCropWatchlistRepository(AgriForecastDbContext db) => _db = db;

    public async Task<List<UserCropWatchlist>> GetAllForUserAsync(
        Guid userId, CancellationToken ct = default)
    {
        return await _db.UserCropWatchlists
            .Where(w => w.UserId == userId)
            .OrderBy(w => w.CreatedAtUtc)
            .ThenBy(w => w.CropId)
            .ToListAsync(ct);
    }

    public async Task AddAsync(UserCropWatchlist entity, CancellationToken ct = default)
        => await _db.UserCropWatchlists.AddAsync(entity, ct);

    public void Remove(UserCropWatchlist entity) => _db.UserCropWatchlists.Remove(entity);
}
