namespace AgriForecast.Application.Services;

// Config seam for the ingestion status handler, so the Application layer needs no direct dependency on
// Microsoft.Extensions.Configuration. The Infrastructure implementation reads the API's configuration and
// applies the fallbacks, so the handler receives already-resolved values.
public interface IIngestionStatusSettings
{
    // Echoed to the admin UI. Resolves to the literal "unconfigured" when the config key is missing.
    string ServiceAddress { get; }

    // Minutes past which an unfinished run counts as crashed rather than running. Defaults to 120.
    int RunningStalenessMinutes { get; }
}
