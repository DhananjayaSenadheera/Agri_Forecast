namespace AgriForecast.Infrastructure.Security;

/// <summary>
/// Bound from the "Jwt" configuration section. In production the Key MUST come from an
/// environment variable / secret store, never from a committed appsettings file.
/// </summary>
public class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>
    /// Lifetime (in days) of the stateless refresh JWT carried in the HttpOnly
    /// "agriforecast_refresh" cookie. Sensible default of 7; override via Jwt:RefreshTokenDays.
    /// </summary>
    public int RefreshTokenDays { get; set; } = 7;

    /// <summary>
    /// Audience stamped on refresh JWTs. It MUST differ from <see cref="Audience"/> so the
    /// normal access-token Bearer pipeline (which validates <see cref="Audience"/>) rejects a
    /// refresh token, and vice-versa. When left empty it is derived as "{Audience}.refresh",
    /// guaranteeing separation with no mandatory extra config.
    /// </summary>
    public string RefreshAudience { get; set; } = string.Empty;

    /// <summary>Effective refresh audience — configured value, or "{Audience}.refresh" fallback.</summary>
    public string EffectiveRefreshAudience =>
        string.IsNullOrWhiteSpace(RefreshAudience) ? $"{Audience}.refresh" : RefreshAudience;
}
