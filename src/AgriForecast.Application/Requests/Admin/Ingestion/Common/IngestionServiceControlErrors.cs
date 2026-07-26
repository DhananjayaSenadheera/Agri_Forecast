namespace AgriForecast.Application.Requests.Admin.Ingestion.Common;

/// <summary>
/// The frozen error codes returned by the ingestion service start/stop endpoints. These are WIRE VALUES,
/// not prose: the admin UI switches on them to choose its message, so they are lowercase snake_case,
/// stable, and never localised or reworded here.
/// <para>
/// The controller maps each of these to HTTP 409 with the body <c>{ "error": "&lt;code&gt;" }</c> — a
/// deliberately different shape from the <c>{ errors: [{ property, message }] }</c> used for validation
/// failures, because these are machine-readable state conflicts rather than field-level complaints.
/// </para>
/// </summary>
public static class IngestionServiceControlErrors
{
    /// <summary>A pass is already in flight (the application lock is held, or a fresh unfinished run row
    /// exists), so a second one must not be started.</summary>
    public const string AlreadyRunning = "already_running";

    /// <summary>Nothing is running, so there is nothing to stop.</summary>
    public const string NotRunning = "not_running";

    /// <summary>Something IS running, but not on this API process — typically the scheduled worker — so the
    /// API has no cancellation token to trip. Reported honestly rather than pretending the stop worked.</summary>
    public const string NotStoppable = "not_stoppable";

    /// <summary>Every code the endpoints can return; the controller uses it to decide 409 vs 400.</summary>
    public static readonly IReadOnlyCollection<string> All = new[]
    {
        AlreadyRunning, NotRunning, NotStoppable
    };

    /// <summary>True when the error is one of the pinned conflict codes (exact, case-sensitive match).</summary>
    public static bool IsConflict(string? error) => error is not null && All.Contains(error);
}
