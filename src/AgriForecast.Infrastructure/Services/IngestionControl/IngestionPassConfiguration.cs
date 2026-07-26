using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.IngestionControl;

/// <summary>
/// Startup guard for the configuration an ingestion pass needs. Called by whichever host can run a pass —
/// the API (admin start button) and the Ingestion Worker — so a host that is missing the Worker-only
/// settings fails LOUDLY at boot instead of at 21:00 as a wall of red run rows.
/// <para>
/// Scope is deliberate. It checks only the STRUCTURAL keys that live in committed appsettings and whose
/// absence is a genuine deployment mistake. It does NOT check secrets: <c>MlService:AdminApiKey</c> is
/// intentionally empty in appsettings and supplied per environment, so refusing to boot the whole API —
/// including every farmer-facing endpoint — over an admin-only feature's secret would be a far worse
/// failure than the one being prevented. A missing admin key already surfaces honestly: each affected
/// source throws a named configuration error, which the audit wrapper records as a Failed run row with
/// that message on the admin ingestion page.
/// </para>
/// </summary>
public static class IngestionPassConfiguration
{
    /// <summary>Keys that must be present and non-blank for a pass to be runnable at all.</summary>
    public static readonly IReadOnlyList<string> RequiredKeys = new[]
    {
        // The Dambulla DEC typed client throws on a missing base URL, and DAMBULLA_DEC is the pass's
        // primary price source.
        "MarketPriceSources:DambullaDec:BaseUrl",
        // Four typed clients (predict, news, HARTI, CBSL) share this one base URL.
        "MlService:BaseUrl"
    };

    /// <summary>The subset of <see cref="RequiredKeys"/> that is missing or blank. Empty means good.</summary>
    public static IReadOnlyList<string> FindMissingKeys(IConfiguration configuration) =>
        RequiredKeys.Where(key => string.IsNullOrWhiteSpace(configuration[key])).ToList();

    /// <summary>
    /// Throws with every missing key named at once, so one restart reveals the whole gap rather than one
    /// key per boot attempt.
    /// </summary>
    public static void ThrowIfIncomplete(IConfiguration configuration)
    {
        var missing = FindMissingKeys(configuration);
        if (missing.Count == 0) return;

        throw new InvalidOperationException(
            "This host can run ingestion passes but its ingestion configuration is incomplete. " +
            $"Missing or blank: {string.Join(", ", missing)}. " +
            "These live in appsettings.json (see AgriForecast.Ingestion/appsettings.json for the shape) " +
            "and can be overridden per environment with the matching double-underscore environment " +
            "variables, e.g. MarketPriceSources__DambullaDec__BaseUrl.");
    }
}
