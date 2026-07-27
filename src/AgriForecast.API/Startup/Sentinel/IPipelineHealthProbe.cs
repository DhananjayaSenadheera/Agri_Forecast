using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;

namespace AgriForecast.API.Startup.Sentinel;

/// <summary>
/// How the sentinel finds out how last night went. One method, so the loop can be tested against a
/// scripted sequence of states without a DI scope, a DbContext or an HTTP call.
/// </summary>
public interface IPipelineHealthProbe
{
    /// <summary>
    /// Reads the current pipeline-health snapshot, or null when it could not be read (a Failure result,
    /// or the query threw). Null is "I do not know", NOT "everything is fine" — the sentinel treats it
    /// as a probe failure and stays quiet rather than inventing a verdict.
    /// </summary>
    Task<PipelineHealth_GetDto?> ReadAsync(CancellationToken cancellationToken);
}
