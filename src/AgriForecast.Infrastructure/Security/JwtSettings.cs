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
}
