using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Services;

public interface IJwtTokenGenerator
{
    /// <summary>
    /// Issues a signed HS256 JWT for the given user. Returns the token string and its UTC expiry.
    /// </summary>
    (string token, DateTime expiresAtUtc) Generate(User user);

    /// <summary>
    /// Issues a signed refresh JWT identifying the user by their immutable id, never the mutable username.
    /// It carries a distinct audience and a token_use=refresh claim, so the Bearer pipeline rejects it.
    /// </summary>
    (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId);

    /// <summary>Overload stamping a caller-supplied jti so the persisted revocation record and the token agree.</summary>
    (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId, Guid jti);

    /// <summary>
    /// Validates a refresh JWT (signature, issuer, refresh audience, lifetime, token_use). Returns the user
    /// id, or null when the token is missing, invalid, expired, or is not a refresh token.
    /// </summary>
    string? ValidateRefreshToken(string? token);

    /// <summary>
    /// As ValidateRefreshToken, but also returns the jti. The jti is read only after cryptographic
    /// validation succeeds, so it can be trusted. Null on any validation failure.
    /// </summary>
    RefreshTokenPrincipal? ReadValidatedRefreshToken(string? token);
}

/// <summary>The trusted identity read from a cryptographically-valid refresh JWT.</summary>
public sealed record RefreshTokenPrincipal(Guid UserId, Guid Jti);
