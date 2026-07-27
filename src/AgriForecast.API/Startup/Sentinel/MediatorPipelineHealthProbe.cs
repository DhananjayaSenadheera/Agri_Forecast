using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using MediatR;

namespace AgriForecast.API.Startup.Sentinel;

/// <summary>
/// Reads pipeline health IN-PROCESS through MediatR — the same GetPipelineHealthQuery the admin banner
/// calls, so the email and the screen can never disagree about a night.
/// <para>Deliberately NOT an HTTP self-call to /api/admin/pipeline/health: that would need the sentinel
/// to mint or hold an admin JWT, would go out through the rate limiter, and would fail whenever the
/// pod's own ingress was the thing that was broken. Sending the query directly has none of those
/// failure modes.</para>
/// <para>The handler's dependencies (the read store, the DbContext behind it) are SCOPED, and this
/// service is a singleton, so every probe opens and disposes its own scope.</para>
/// </summary>
public class MediatorPipelineHealthProbe : IPipelineHealthProbe
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MediatorPipelineHealthProbe> _logger;

    public MediatorPipelineHealthProbe(
        IServiceScopeFactory scopeFactory,
        ILogger<MediatorPipelineHealthProbe> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PipelineHealth_GetDto?> ReadAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();

        var result = await mediator.Send(new GetPipelineHealthQuery(), cancellationToken);

        if (!result.IsSuccess || result.Data is null)
        {
            // The handler currently has no failure path, but a Result<T> that says Failure must never be
            // silently read as a healthy snapshot.
            _logger.LogWarning(
                "Pipeline sentinel: health query returned no snapshot ({Error}).", result.Error);
            return null;
        }

        return result.Data;
    }
}
