namespace AgriForecast.Application.Services;

/// <summary>
/// Owns the persisted refresh-token (jti / token-family) revocation state machine. Called by the
/// AuthController (issue on login or register, rotate on refresh, revoke the family on logout) and by the
/// admin user handlers (revoke all on delete or demote). Fail-closed: if the store cannot be reached
/// during a rotate the rotation fails — there is no silent stateless fallback.
/// </summary>
public interface IRefreshTokenService
{
    /// <summary>
    /// Issues a refresh token in a brand-new family and persists its record. Returns a null token when the
    /// record could not be persisted — the caller then omits the cookie, and a token that was never stored
    /// could not be honoured anyway.
    /// </summary>
    Task<(string? token, DateTime expiresAtUtc)> IssueNewFamilyAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Validates a presented refresh token against both crypto and the store (the jti row must exist, be
    /// unexpired, unrevoked and unused), then rotates it: the presented row is marked Used and a child token
    /// is issued in the same family. Presenting an already-used or revoked token is a reuse signal and
    /// revokes the entire family. Fail-closed on any store error.
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
