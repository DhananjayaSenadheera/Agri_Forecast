using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

/// <summary>
/// EF Core store for <see cref="RefreshTokenRecord"/>. Follows the direct-DbContext repository style
/// (like IngestionWatermarkRepository) rather than the generic repo, because the revocation/purge
/// operations are set-based (ExecuteUpdate/ExecuteDelete run as one SQL statement, no change
/// tracking) while rotation needs a tracked row to flip <c>UsedAtUtc</c>.
/// </summary>
public class RefreshTokenRepository : IRefreshTokenRepository
{
    private readonly AgriForecastDbContext _db;

    public RefreshTokenRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(RefreshTokenRecord record, CancellationToken ct = default)
    {
        await _db.RefreshTokens.AddAsync(record, ct);
    }

    public async Task<RefreshTokenRecord?> GetByJtiAsync(Guid jti, CancellationToken ct = default)
    {
        // Tracked (no AsNoTracking) so the caller can mutate UsedAtUtc and SaveChanges it.
        return await _db.RefreshTokens.FirstOrDefaultAsync(r => r.Jti == jti, ct);
    }

    public async Task<int> TryMarkUsedAsync(Guid jti, DateTime usedAtUtc, CancellationToken ct = default)
    {
        // Atomic compare-and-set as ONE SQL UPDATE: only a currently-unused, unrevoked row
        // transitions to Used. Under concurrency exactly one caller sees 1 affected row — this is
        // what prevents two simultaneous rotations of the same (stolen) token from silently
        // forking the family past reuse detection.
        return await _db.RefreshTokens
            .Where(r => r.Jti == jti && r.UsedAtUtc == null && r.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UsedAtUtc, usedAtUtc), ct);
    }

    public async Task<int> RevokeFamilyAsync(Guid familyId, DateTime revokedAtUtc, CancellationToken ct = default)
    {
        return await _db.RefreshTokens
            .Where(r => r.FamilyId == familyId && r.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAtUtc, revokedAtUtc), ct);
    }

    public async Task<int> RevokeAllForUserAsync(Guid userId, DateTime revokedAtUtc, CancellationToken ct = default)
    {
        return await _db.RefreshTokens
            .Where(r => r.UserId == userId && r.RevokedAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RevokedAtUtc, revokedAtUtc), ct);
    }

    public async Task<int> PurgeExpiredAsync(DateTime expiredBeforeUtc, CancellationToken ct = default)
    {
        return await _db.RefreshTokens
            .Where(r => r.ExpiresAtUtc < expiredBeforeUtc)
            .ExecuteDeleteAsync(ct);
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
