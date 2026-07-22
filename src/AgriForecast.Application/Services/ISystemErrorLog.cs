namespace AgriForecast.Application.Services;

// Fire-safe writer for the SystemErrors table (Logs hub PR A / Phase 3). Called from
// GlobalExceptionMiddleware's general-exception path (500s only). The implementation
// (SystemErrorLog) isolates every insert in its OWN service scope, swallows-and-logs, and NEVER throws
// into the request pipeline — error logging must never change the response it is recording.
public interface ISystemErrorLog
{
    /// <summary>
    /// Records an unhandled exception. Contract: this call NEVER throws (a write/prune failure is
    /// swallowed-and-logged). It captures only the exception type/message/stack, the request method,
    /// and the request PATH (the caller MUST pass a bare path, never a query string), plus the trace
    /// id — no request field (query string, header, body) is ever captured directly. NOTE: the
    /// exception message and stack trace are stored VERBATIM (length-capped) and may contain sensitive
    /// substrings if upstream code embeds them; accepted risk, mitigated by Admin-only read access.
    /// A process-wide storm guard may DROP the write under a burst; a periodic retention prune trims
    /// rows older than 90 days.
    /// </summary>
    Task RecordAsync(Exception ex, string method, string path, string? traceId, CancellationToken ct);
}
