using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

/// <summary>
/// Persistence surface for <see cref="RefreshTokenRecord"/> — the refresh-token revocation store.
/// Family/user-wide revocation and expiry purge are expressed as set-based operations so they run
/// as single UPDATE/DELETE statements (no per-row change tracking); <see cref="GetByJtiAsync"/>
/// returns a TRACKED entity so the rotation flow can flip <c>UsedAtUtc</c> and
/// <see cref="SaveChangesAsync"/> the change alongside the newly-added child row.
/// </summary>
public interface IRefreshTokenRepository
{
    /// <summary>Stages a new record for insert. Persisted by <see cref="SaveChangesAsync"/>.</summary>
    Task AddAsync(RefreshTokenRecord record, CancellationToken ct = default);

    /// <summary>Loads the record for a jti as a TRACKED entity (mutations are saved on the next SaveChanges). Null if unknown.</summary>
    Task<RefreshTokenRecord?> GetByJtiAsync(Guid jti, CancellationToken ct = default);

    /// <summary>
    /// Atomic compare-and-set: marks the row Used ONLY if it is currently unused and unrevoked,
    /// as a single UPDATE. Returns the affected-row count — exactly one of any concurrent callers
    /// can observe 1; the rest observe 0 (treat as reuse). Executes immediately.
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
