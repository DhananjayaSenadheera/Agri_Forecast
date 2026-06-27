using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Services;

public interface IJwtTokenGenerator
{
    /// <summary>
    /// Issues a signed HS256 JWT for the given user. Returns the token string and its UTC expiry.
    /// </summary>
    (string token, DateTime expiresAtUtc) Generate(User user);
}
