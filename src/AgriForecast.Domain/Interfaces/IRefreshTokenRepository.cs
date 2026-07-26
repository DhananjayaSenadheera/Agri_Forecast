using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

/// <summary>
/// Persistence for refresh-token records. Family- and user-wide revoke and the expiry purge are set-based
/// (a single UPDATE/DELETE, no change tracking); GetByJtiAsync returns a TRACKED entity so the rotation
/// flow can flip UsedAtUtc and save it alongside the newly-added child row.
/// </summary>
public interface IRefreshTokenRepository
{
    /// <summary>Stages a new record for insert. Persisted by <see cref="SaveChangesAsync"/>.</summary>
    Task AddAsync(RefreshTokenRecord record, CancellationToken ct = default);

    /// <summary>Loads the record for a jti as a TRACKED entity (mutations are saved on the next SaveChanges). Null if unknown.</summary>
    Task<RefreshTokenRecord?> GetByJtiAsync(Guid jti, CancellationToken ct = default);

    /// <summary>
    /// Atomic compare-and-set: marks the row Used only if it is currently unused and unrevoked. Exactly
    /// one of any concurrent callers sees a count of 1; the rest see 0 and must treat it as reuse.
    /// </summary>
    Task<int> TryMarkUsedAsync(Guid jti, DateTime usedAtUtc, CancellationToken ct = default);

    /// <summary>Set-based revoke of every not-yet-revoked row in a family. Executes immediately.</summary>
    Task<int> RevokeFamilyAsync(Guid familyId, DateTime revokedAtUtc, CancellationToken ct = default);

    /// <summary>Set-based revoke of every not-yet-revoked row for a user. Executes immediately.</summary>
    Task<int> RevokeAllForUserAsync(Guid userId, DateTime revokedAtUtc, CancellationToken ct = default);

    /// <summary>Set-based delete of rows whose expiry is older than the cutoff. Housekeeping; executes immediately.</summary>
    Task<int> PurgeExpiredAsync(DateTime expiredBeforeUtc, CancellationToken ct = default);

    /// <summary>Flushes staged inserts / tracked mutations.</summary>
    Task SaveChangesAsync(CancellationToken ct = default);
}
