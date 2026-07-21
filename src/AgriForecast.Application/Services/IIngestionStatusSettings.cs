namespace AgriForecast.Application.Services;

// Config seam for the ingestion status handler, so the Application layer stays free of a direct
// Microsoft.Extensions.Configuration dependency (the house dependency rule — config is resolved at
// the Infrastructure boundary). The Infrastructure implementation reads the API's own configuration
// (Ingestion:ServiceAddress / Ingestion:RunningStalenessMinutes) and applies the documented
// fallbacks, so the handler receives already-resolved values and is trivially unit-testable.
public interface IIngestionStatusSettings
{
    // Echoed to the admin UI as the identity of the process that runs ingestion. Resolves to the
    // literal "unconfigured" when the config key is absent/blank (never null/empty).
    string ServiceAddress { get; }

    // Window (minutes) past which an unfinished run is treated as crashed (state "stopped") rather
    // than "running". Resolves to 120 when the config key is absent.
    int RunningStalenessMinutes { get; }
}
