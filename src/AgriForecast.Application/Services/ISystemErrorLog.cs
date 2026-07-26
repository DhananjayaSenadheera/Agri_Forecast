namespace AgriForecast.Application.Services;

// Fire-safe writer for the SystemErrors table, called from GlobalExceptionMiddleware's 500 path. The
// implementation isolates every insert in its own service scope, swallows-and-logs, and never throws into
// the request pipeline — error logging must not change the response it is recording.
public interface ISystemErrorLog
{
    /// <summary>
    /// Records an unhandled exception. This call never throws: a write or prune failure is swallowed and
    /// logged. It captures only the exception type, message and stack, the request method and PATH (the
    /// caller MUST pass a bare path, never a query string), and the trace id. The message and stack are
    /// stored verbatim, so they may carry whatever upstream code embedded in them — an accepted risk,
    /// mitigated by Admin-only read access. A storm guard may drop the write under a burst, and a periodic
    /// prune trims rows older than 90 days.
    /// </summary>
    Task RecordAsync(Exception ex, string method, string path, string? traceId, CancellationToken ct);
}
