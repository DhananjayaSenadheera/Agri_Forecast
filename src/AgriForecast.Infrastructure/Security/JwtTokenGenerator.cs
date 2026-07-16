using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AgriForecast.Infrastructure.Security;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly JwtSettings _settings;

    public JwtTokenGenerator(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;
    }

    public (string token, DateTime expiresAtUtc) Generate(User user)
    {
        var expiresAtUtc = DateTime.UtcNow.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new[]
        {
            // GUIDs are normalised to lowercase on the wire, matching the .NET<->Python contract convention.
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return (tokenString, expiresAtUtc);
    }

    // The refresh token marker claim. Its presence (and value) is required by the refresh
    // endpoint; an access token (which lacks it and carries a different audience) is rejected.
    private const string TokenUseClaim = "token_use";
    private const string RefreshTokenUse = "refresh";

    // Internal-jti overload: unchanged public behaviour for the stateless call sites/tests.
    public (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId)
        => GenerateRefreshToken(userId, Guid.NewGuid());

    public (string token, DateTime expiresAtUtc) GenerateRefreshToken(Guid userId, Guid jti)
    {
        // Refresh tokens are long-lived and identify the user by their IMMUTABLE id only — never the
        // mutable username — so a rename or delete-then-re-register can't let an outstanding refresh
        // token resolve to a different account. Username/email/role are re-resolved from the store on
        // each refresh, so they are deliberately NOT embedded. Id is lowercased (Guid.ToString()
        // default) to match the .NET<->Python wire convention. The jti is supplied by the caller so
        // the persisted revocation record and this token agree on the same identifier.
        var expiresAtUtc = DateTime.UtcNow.AddDays(_settings.RefreshTokenDays);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, jti.ToString()),
            // Token-type separation: marks this as a refresh token so it can never be replayed
            // as an access token even if the audience check were ever loosened.
            new Claim(TokenUseClaim, RefreshTokenUse)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            // Distinct audience so the default JwtBearer pipeline (ValidAudience = access audience)
            // rejects this token if it is ever presented as a Bearer access token.
            audience: _settings.EffectiveRefreshAudience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAtUtc,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(token);
        return (tokenString, expiresAtUtc);
    }

    public string? ValidateRefreshToken(string? token)
    {
        var validated = ValidateRefreshCore(token);
        if (validated is null)
            return null;

        // Subject is the immutable user id. Default inbound mapping turns "sub" into
        // ClaimTypes.NameIdentifier, so check both to be robust to mapping settings.
        var userId = validated.Value.principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? validated.Value.principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        return string.IsNullOrWhiteSpace(userId) ? null : userId;
    }

    public RefreshTokenPrincipal? ReadValidatedRefreshToken(string? token)
    {
        var validated = ValidateRefreshCore(token);
        if (validated is null)
            return null;

        var (principal, jwt) = validated.Value;
        var userIdStr = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        // jwt.Id is the raw jti claim, read straight off the validated token — immune to inbound
        // claim-type remapping. Both ids must be well-formed GUIDs or the token is treated as invalid.
        if (!Guid.TryParse(userIdStr, out var userId) || !Guid.TryParse(jwt.Id, out var jti))
            return null;

        return new RefreshTokenPrincipal(userId, jti);
    }

    // Shared crypto validation for both refresh-token readers. Returns the validated principal and
    // the parsed token, or null on ANY failure (bad signature / wrong audience / expired / malformed
    // / not a refresh token) — the reason is never surfaced, so callers cannot build an oracle.
    private (ClaimsPrincipal principal, JwtSecurityToken jwt)? ValidateRefreshCore(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return null;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _settings.Issuer,
            // Only the refresh audience is accepted here, so an ACCESS token placed in the cookie
            // (audience = access audience) fails validation and is rejected as a refresh token.
            ValidAudience = _settings.EffectiveRefreshAudience,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        try
        {
            var principal = new JwtSecurityTokenHandler()
                .ValidateToken(token, validationParameters, out var validated);

            if (validated is not JwtSecurityToken jwt)
                return null;

            // Defence in depth on top of the audience check.
            var tokenUse = principal.FindFirst(TokenUseClaim)?.Value;
            if (!string.Equals(tokenUse, RefreshTokenUse, StringComparison.Ordinal))
                return null;

            return (principal, jwt);
        }
        catch
        {
            // Any validation failure (bad signature, wrong audience, expired, malformed) is a 401 —
            // never surface the reason to the caller.
            return null;
        }
    }
}
