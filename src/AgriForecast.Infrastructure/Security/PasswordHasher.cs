using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using Identity = Microsoft.AspNetCore.Identity;

namespace AgriForecast.Infrastructure.Security;

/// <summary>
/// PBKDF2-based password hashing, delegating to ASP.NET Core Identity's vetted hasher
/// so we never hand-roll crypto. Salt is embedded in the produced hash string.
/// </summary>
public class PasswordHasher : IPasswordHasher
{
    private readonly Identity.PasswordHasher<User> _inner = new();

    public string Hash(string password)
    {
        // The user instance is not used by the underlying algorithm; pass a throwaway.
        return _inner.HashPassword(new User(), password);
    }

    public bool Verify(string hash, string password)
    {
        try
        {
            var result = _inner.VerifyHashedPassword(new User(), hash, password);
            return result is Identity.PasswordVerificationResult.Success
                or Identity.PasswordVerificationResult.SuccessRehashNeeded;
        }
        catch (FormatException)
        {
            // A malformed / non-base64 stored hash (corrupted row, garbage input)
            // is a mismatch, not a crash — never let it surface as an unhandled exception.
            return false;
        }
    }
}
