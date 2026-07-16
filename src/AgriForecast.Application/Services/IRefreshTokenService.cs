namespace AgriForecast.Application.Services;

/// <summary>
/// Owns the persisted refresh-token (jti / token-family) revocation state machine that upgrades the
/// refresh flow from stateless to revocable. Callers: the AuthController (issue on login/register,
/// rotate on /refresh, revoke-family on logout) and the admin user handlers (revoke-all on
/// delete/demote). Fail-CLOSED: if the backing store cannot be reached during a rotate, the rotation
/// fails — there is no silent stateless fallback.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Issues a refresh token in a BRAND-NEW family (login / register) and persists its record.
    /// Returns the raw token + expiry, or a null token when the record could not be persisted (the
    /// caller then simply omits the cookie — access-token login still succeeds, and a token that was
    /// never stored could never be honoured anyway).
    /// </summary>
    Task<(string? token, DateTime expiresAtUtc)> IssueNewFamilyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Validates a presented refresh token (crypto AND store: jti row must exist, be unexpired, not
    /// revoked, not already used) and, on success, rotates it: marks the presented row Used and issues
    /// a child token in the SAME family. Presenting an already-used/revoked token is a reuse/theft
    /// signal and revokes the ENTIRE family. Fail-closed on any store error.
    /// </summary>
    Task<RefreshRotationResult> RotateAsync(string? rawToken, CancellationToken ct = default);

    /// <summary>Logout: revokes the family the presented token belongs to. No-op for a missing/invalid/unknown token.</summary>
    Task RevokeFamilyForTokenAsync(string? rawToken, CancellationToken ct = default);

    /// <summary>Admin delete/demote: revokes every family for a user so no outstanding refresh token survives.</summary>
    Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>Outcome of <see cref="IRefreshTokenService.RotateAsync"/>. On success carries the new child refresh token.</summary>
public sealed record RefreshRotationResult(bool IsSuccess, Guid UserId, string? Token, DateTime ExpiresAtUtc)
{
    public static RefreshRotationResult Fail() => new(false, Guid.Empty, null, default);
    public static RefreshRotationResult Success(Guid userId, string token, DateTime expiresAtUtc)
        => new(true, userId, token, expiresAtUtc);
}
