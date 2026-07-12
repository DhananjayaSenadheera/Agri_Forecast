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
    /// Jwt:RefreshTokenDays. Returns the token and UTC expiry.
    /// </summary>
    (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId);

    /// <summary>
    /// Validates a refresh JWT (signature, issuer, refresh audience, lifetime, token_use=refresh).
    /// Returns the user id (as a string) it identifies, or null when the token is missing/invalid/
    /// expired or is not a refresh token (e.g. an access token placed in the cookie).
    /// </summary>
    string? ValidateRefreshToken(string? token);
}
