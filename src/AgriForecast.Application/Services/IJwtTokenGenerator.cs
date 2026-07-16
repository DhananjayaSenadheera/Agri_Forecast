using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Services;

public interface IJwtTokenGenerator
{
    /// <summary>
    /// Issues a signed HS256 JWT for the given user. Returns the token string and its UTC expiry.
    /// </summary>
    (string token, DateTime expiresAtUtc) Generate(User user);

    /// <summary>
    /// Issues a signed HS256 REFRESH JWT identifying the given user by their IMMUTABLE id (never the
    /// mutable username). The refresh token carries a distinct audience and a token_use=refresh claim
    /// so it is rejected by the normal access-token (Bearer) pipeline. Lifetime comes from
    /// Jwt:RefreshTokenDays. Returns the token and UTC expiry. The jti is generated internally.
    /// </summary>
    (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId);

    /// <summary>
    /// Overload that stamps a CALLER-SUPPLIED jti so the persisted revocation record and the token
    /// agree on the same jti. Used by the refresh-token revocation store (issue + rotate).
    /// </summary>
    (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId, Guid jti);

    /// <summary>
    /// Validates a refresh JWT (signature, issuer, refresh audience, lifetime, token_use=refresh).
    /// Returns the user id (as a string) it identifies, or null when the token is missing/invalid/
    /// expired or is not a refresh token (e.g. an access token placed in the cookie).
    /// </summary>
    string? ValidateRefreshToken(string? token);

    /// <summary>
    /// Same validation as <see cref="ValidateRefreshToken"/> but also returns the token's jti, so the
    /// revocation store can look up the persisted record. Null on any validation failure. The jti is
    /// read only AFTER cryptographic validation succeeds, so it can be trusted.
    /// </summary>
    RefreshTokenPrincipal? ReadValidatedRefreshToken(string? token);
}

/// <summary>The trusted identity read from a cryptographically-valid refresh JWT.</summary>
public sealed record RefreshTokenPrincipal(Guid UserId, Guid Jti);
