using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Security;

/// <summary>
/// The persisted refresh-token (jti / token-family) revocation state machine.
///
/// State machine:
///  - ISSUE  (login/register): mint a refresh JWT with a fresh jti in a NEW family; persist a row.
///  - ROTATE (/refresh): the presented token must be crypto-valid AND its jti row must exist, be
///    unexpired, not revoked, and not already used. On success mark the row Used and issue a child
///    row with the SAME family (chaining). A used/revoked row presented again is a reuse/theft
///    signal ⇒ revoke the ENTIRE family and reject.
///  - LOGOUT: revoke the presented token's family.
///  - DELETE/DEMOTE (admin): revoke every family for the user.
///
/// Fail-CLOSED: any store error during ROTATE rejects the refresh (no stateless fallback).
/// LOGGING: user id / family id only — never the raw token or the jti (both are token identifiers).
/// </summary>
public class RefreshTokenService : IRefreshTokenService
{
    // Opportunistic housekeeping: on each successful issue/rotation, delete rows that expired more than
    // this grace beyond their expiry. Keeps the tiny table from accumulating dead rows without a
    // background service. Grace (not exactly-at-expiry) leaves a short window for any last
    // reuse-detection to still find a just-expired row.
    private const int PurgeGraceDays = 2;

    private readonly IRefreshTokenRepository _store;
    private readonly IJwtTokenGenerator _jwt;
    private readonly ILogger<RefreshTokenService> _logger;

    public RefreshTokenService(
        IRefreshTokenRepository store,
        IJwtTokenGenerator jwt,
        ILogger<RefreshTokenService> logger)
    {
        _store = store;
        _jwt = jwt;
        _logger = logger;
    }

    public async Task<(string? token, DateTime expiresAtUtc)> IssueNewFamilyAsync(Guid userId, CancellationToken ct = default)
    {
        var jti = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        var (token, expiresAtUtc) = _jwt.GenerateRefreshToken(userId, jti);

        try
        {
            await _store.AddAsync(new RefreshTokenRecord
            {
                Id = Guid.NewGuid(),
                Jti = jti,
                FamilyId = familyId,
                UserId = userId,
                IssuedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = expiresAtUtc
            }, ct);
            await _store.SaveChangesAsync(ct);
            // Purge on issue as well as rotate: a deployment where users log in but rarely hit
            // /refresh would otherwise accumulate dead rows until some rotation happens to run.
            await TryPurgeExpiredAsync(ct);
            return (token, expiresAtUtc);
        }
        catch (Exception ex)
        {
            // Do not fail the login/register itself — the access token is already minted. A refresh
            // token that could not be persisted could never be honoured anyway, so omit the cookie
            // (fail-safe, no stateless bypass). User re-authenticates when the access token expires.
            _logger.LogWarning(ex, "Could not persist refresh-token record on issue. UserId: {UserId}", userId);
            return (null, default);
        }
    }

    public async Task<RefreshRotationResult> RotateAsync(string? rawToken, CancellationToken ct = default)
    {
        // 1) Cryptographic validation (signature / issuer / refresh-audience / lifetime / token_use).
        var principal = _jwt.ReadValidatedRefreshToken(rawToken);
        if (principal is null)
        {
            _logger.LogInformation("Refresh rejected: missing or cryptographically invalid refresh token.");
            return RefreshRotationResult.Fail();
        }

        try
        {
            // 2) Store lookup by jti. Unknown jti (e.g. a valid-signature token whose row was purged
            //    or whose user was deleted) ⇒ fail-closed.
            var row = await _store.GetByJtiAsync(principal.Jti, ct);
            if (row is null)
            {
                _logger.LogInformation("Refresh rejected: no store record. UserId: {UserId}", principal.UserId);
                return RefreshRotationResult.Fail();
            }

            // 3) Reuse detection: a token whose row is already used or revoked is being replayed —
            //    a theft signal. Revoke the WHOLE family and reject.
            if (row.UsedAtUtc is not null || row.RevokedAtUtc is not null)
            {
                await _store.RevokeFamilyAsync(row.FamilyId, DateTime.UtcNow, ct);
                _logger.LogWarning(
                    "Refresh token reuse detected — family revoked. UserId: {UserId} FamilyId: {FamilyId}",
                    row.UserId, row.FamilyId);
                return RefreshRotationResult.Fail();
            }

            // 4) Expiry defence in depth (crypto lifetime already checked, but never trust one gate).
            if (row.ExpiresAtUtc <= DateTime.UtcNow)
            {
                _logger.LogInformation("Refresh rejected: store record expired. UserId: {UserId}", row.UserId);
                return RefreshRotationResult.Fail();
            }

            // 5) Rotate: atomically mark the presented row Used (compare-and-set — exactly one of
            //    any concurrent callers wins). Losing the race means someone else just spent this
            //    jti in the last few milliseconds: that is the same theft signal as step 3, so
            //    revoke the family. Without this, two simultaneous rotations of a stolen token
            //    would both succeed and fork the family past reuse detection.
            var now = DateTime.UtcNow;
            var marked = await _store.TryMarkUsedAsync(row.Jti, now, ct);
            if (marked == 0)
            {
                await _store.RevokeFamilyAsync(row.FamilyId, now, ct);
                _logger.LogWarning(
                    "Refresh token concurrent reuse detected — family revoked. UserId: {UserId} FamilyId: {FamilyId}",
                    row.UserId, row.FamilyId);
                return RefreshRotationResult.Fail();
            }

            var childJti = Guid.NewGuid();
            var (childToken, childExpiresAt) = _jwt.GenerateRefreshToken(row.UserId, childJti);
            await _store.AddAsync(new RefreshTokenRecord
            {
                Id = Guid.NewGuid(),
                Jti = childJti,
                FamilyId = row.FamilyId,
                UserId = row.UserId,
                IssuedAtUtc = now,
                ExpiresAtUtc = childExpiresAt
            }, ct);

            await _store.SaveChangesAsync(ct);

            await TryPurgeExpiredAsync(ct);

            return RefreshRotationResult.Success(row.UserId, childToken, childExpiresAt);
        }
        catch (Exception ex)
        {
            // Fail-CLOSED: a store error rejects the refresh. Never fall back to a stateless accept.
            _logger.LogWarning(ex, "Refresh rejected: token store unavailable. UserId: {UserId}", principal.UserId);
            return RefreshRotationResult.Fail();
        }
    }

    public async Task RevokeFamilyForTokenAsync(string? rawToken, CancellationToken ct = default)
    {
        var principal = _jwt.ReadValidatedRefreshToken(rawToken);
        if (principal is null)
            return; // nothing to revoke for a missing/invalid token — logout still clears the cookie.

        try
        {
            var row = await _store.GetByJtiAsync(principal.Jti, ct);
            if (row is null)
                return;

            await _store.RevokeFamilyAsync(row.FamilyId, DateTime.UtcNow, ct);
            _logger.LogInformation(
                "Logout: refresh family revoked. UserId: {UserId} FamilyId: {FamilyId}",
                row.UserId, row.FamilyId);
        }
        catch (Exception ex)
        {
            // Logout is best-effort on the server side; the cookie is cleared regardless by the caller.
            _logger.LogWarning(ex, "Logout: could not revoke refresh family. UserId: {UserId}", principal.UserId);
        }
    }

    public async Task RevokeAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var revoked = await _store.RevokeAllForUserAsync(userId, DateTime.UtcNow, ct);
        if (revoked > 0)
            _logger.LogInformation(
                "All refresh families revoked for user (admin delete/demote). UserId: {UserId} Families: {Count}",
                userId, revoked);
    }

    private async Task TryPurgeExpiredAsync(CancellationToken ct)
    {
        try
        {
            await _store.PurgeExpiredAsync(DateTime.UtcNow.AddDays(-PurgeGraceDays), ct);
        }
        catch (Exception ex)
        {
            // Housekeeping only — a purge failure must never fail the refresh it piggybacks on.
            _logger.LogWarning(ex, "Opportunistic refresh-token purge failed (non-fatal).");
        }
    }
}
