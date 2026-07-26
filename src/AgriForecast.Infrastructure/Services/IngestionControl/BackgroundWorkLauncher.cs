using AgriForecast.Application.Services;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.IngestionControl;

// Thread-pool implementation of the fire-and-forget seam. Singleton: it holds no state and must be
// resolvable from a request scope that is about to be disposed.
//
// The try/catch is the whole point of having a class here. A bare `_ = Task.Run(work)` leaves a faulted
// task unobserved, so a pass that dies on an unexpected exception would disappear without a log line and
// the admin would just see a run row stuck at Running.
public class BackgroundWorkLauncher : IBackgroundWorkLauncher
{
    private readonly ILogger<BackgroundWorkLauncher> _logger;

    public BackgroundWorkLauncher(ILogger<BackgroundWorkLauncher> logger) => _logger = logger;

    public void Run(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background work faulted after the request that started it had returned.");
            }
        });
    }
}
