using AgriForecast.Application.Services;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.IngestionRead;

// Resolves the ingestion status handler's config values from the API's own configuration, applying
// the documented fallbacks. Kept at the Infrastructure boundary so the Application layer never takes
// a Microsoft.Extensions.Configuration dependency (house dependency rule). Hosted deployments
// override via env (Ingestion__ServiceAddress / Ingestion__RunningStalenessMinutes).
public class IngestionStatusSettings : IIngestionStatusSettings
{
    private const string ServiceAddressKey = "Ingestion:ServiceAddress";
    private const string StalenessKey = "Ingestion:RunningStalenessMinutes";
    private const string UnconfiguredServiceAddress = "unconfigured";
    private const int DefaultStalenessMinutes = 120;

    public string ServiceAddress { get; }
    public int RunningStalenessMinutes { get; }

    public IngestionStatusSettings(IConfiguration configuration)
    {
        var address = configuration[ServiceAddressKey];
        ServiceAddress = string.IsNullOrWhiteSpace(address) ? UnconfiguredServiceAddress : address;

        // Read the raw string and TryParse rather than GetValue<int?> — a non-numeric value (e.g.
        // "abc") makes GetValue throw at construction (a 500 on the status endpoint). Fall back to
        // the default on absent, non-numeric, OR non-positive values (a zero/negative window would
        // make every unfinished run look crashed).
        var rawStaleness = configuration[StalenessKey];
        RunningStalenessMinutes =
            int.TryParse(rawStaleness, out var parsed) && parsed > 0
                ? parsed
                : DefaultStalenessMinutes;
    }
}
