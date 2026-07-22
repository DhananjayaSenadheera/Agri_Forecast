namespace AgriForecast.Domain.Entities;

// One row per unhandled server-side error -> table SystemErrors (Logs hub PR A / Phase 3). WRITTEN by
// the .NET side via ISystemErrorLog from GlobalExceptionMiddleware's general-exception path (500s only —
// a 400/ValidationException is a client error and is NEVER recorded here); READ by the admin Logs hub
// (AdminLogsController). A logging failure must NEVER change the response, so the write side
// (SystemErrorLog) isolates every insert in its own service scope and swallows-and-logs — this entity
// just guarantees a row can never be built into an inconsistent or leaky state.
//
// PRIVACY: only the exception TYPE, its MESSAGE, its STACK TRACE, and the request METHOD + PATH (path
// ONLY — the caller must never pass a query string) + the trace id are captured. No request field is
// ever captured directly: no query string, header, or body column exists. HOWEVER, Message and
// StackTrace are persisted VERBATIM (length-capped only) — if upstream code embeds a secret, token,
// username, or connection detail in an exception message, it WILL land here. Accepted risk, mitigated
// only by Admin-only read access and the length caps; upstream code must never interpolate secrets
// into exception messages. Every text column is hard-capped to its length by the factory (StackTrace
// to 8000 chars though the column is nvarchar(max)) so an over-long value can never reach SQL and
// turn the error-log write into its own failure.
//
// Setters are private and rows are built via the single FromException factory (house style, as
// UserActivityEvent's intent factories) so a row can never be poked into a leaky or overflowing state.
// occurredUtc is passed in (never an internal UtcNow) so the caller controls the clock and tests are
// deterministic.
public class SystemError
{
    // bigint identity (DB-generated) — this table is append-only and can grow large (retention prunes it).
    public long Id { get; private set; }

    public DateTime OccurredUtc { get; private set; }

    // The originating subsystem (e.g. "API"). Code-authored constant, never user input.
    public string Source { get; private set; } = string.Empty;

    // The exception's CLR type name (e.g. "System.InvalidOperationException").
    public string ExceptionType { get; private set; } = string.Empty;

    // The exception message. Capped to the column length; a blank message stores null.
    public string? Message { get; private set; }

    // The full stack trace. Column is nvarchar(max) but the factory hard-caps to 8000 chars.
    public string? StackTrace { get; private set; }

    // The request PATH ONLY (never a query string — the caller must strip it). Capped; blank -> null.
    public string? Path { get; private set; }

    // The HTTP method (GET/POST/...). Capped; blank -> null.
    public string? Method { get; private set; }

    // The framework trace identifier correlating this row to the 500 response body. Capped; blank -> null.
    public string? TraceId { get; private set; }

    // nvarchar column caps shared by the factory. StackTrace is nvarchar(max) in SQL but the factory
    // still hard-caps it so a pathological trace can never bloat the table or the write.
    private const int SourceMaxLength = 20;
    private const int ExceptionTypeMaxLength = 200;
    private const int MessageMaxLength = 1000;
    private const int StackTraceMaxLength = 8000;
    private const int PathMaxLength = 200;
    private const int MethodMaxLength = 10;
    private const int TraceIdMaxLength = 50;

    private SystemError() { }

    // The single intent factory. Extracts ONLY the type/message/stack from the exception and the
    // method/path/traceId from the caller, enforcing every column cap (trim, truncate, blank -> null)
    // so an over-long value can NEVER reach SQL and throw. path MUST be a bare request path — the caller
    // is responsible for never passing a query string.
    public static SystemError FromException(
        Exception exception,
        string source,
        string? method,
        string? path,
        string? traceId,
        DateTime occurredUtc) => new()
        {
            OccurredUtc = occurredUtc,
            Source = CapRequired(source, SourceMaxLength, "UNKNOWN"),
            ExceptionType = CapRequired(
                exception.GetType().FullName ?? exception.GetType().Name, ExceptionTypeMaxLength, "UnknownException"),
            Message = Cap(exception.Message, MessageMaxLength),
            StackTrace = Cap(exception.StackTrace, StackTraceMaxLength),
            Path = Cap(path, PathMaxLength),
            Method = Cap(method, MethodMaxLength),
            TraceId = Cap(traceId, TraceIdMaxLength)
        };

    // Trim then hard-cap to the column length; a blank/empty value stores null (never a fabricated
    // empty string). Guards against a column-overflow write turning an error-log save into a failure.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    // As Cap but for a non-nullable column: a blank value falls back to a fixed sentinel so the
    // required column is never null.
    private static string CapRequired(string? raw, int maxLength, string fallback) =>
        Cap(raw, maxLength) ?? fallback;
}
