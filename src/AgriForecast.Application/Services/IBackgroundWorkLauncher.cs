namespace AgriForecast.Application.Services;

// Starts work that must OUTLIVE the request that asked for it, and returns immediately.
//
// The ingestion start endpoint answers 202 in milliseconds while the pass it kicked off runs for minutes,
// so the handler cannot await it. Going through this seam rather than a bare Task.Run in the handler buys
// two things: the fire-and-forget has ONE place that observes and logs a faulted task (an unobserved
// exception in a background pass would otherwise vanish), and tests can substitute a launcher that
// captures the work and runs it deterministically instead of racing a thread-pool thread.
//
// The work must NOT capture the request's CancellationToken — that token is tripped when the response
// completes, which would cancel the pass the instant the 202 was written. Pass a token owned by the work
// itself (see IApiHostedPassHandle.Token).
public interface IBackgroundWorkLauncher
{
    void Run(Func<Task> work);
}
