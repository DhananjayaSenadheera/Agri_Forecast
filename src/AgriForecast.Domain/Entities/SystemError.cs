namespace AgriForecast.Domain.Entities;

// One row per unhandled server-side error (table SystemErrors). Written by GlobalExceptionMiddleware for
// 500s only — a validation/400 is a client error and is never recorded here — and read by the admin Logs
// hub. A logging failure must never change the response, so the write side swallows and logs.
//
// Privacy: only the exception type, message and stack trace plus the request method, path (path only,
// never a query string) and trace id are captured. Message and StackTrace are stored verbatim, so
// upstream code must never interpolate secrets into exception messages. Every text column is capped by
// the factory so an over-long value cannot turn the error-log write into its own failure.
public class SystemError
{
    // bigint identity; the table is append-only and pruned by retention.
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

    // Column caps shared by the factory. StackTrace is nvarchar(max) in SQL but is still capped here.
    private const int SourceMaxLength = 20;
    private const int ExceptionTypeMaxLength = 200;
    private const int MessageMaxLength = 1000;
    private const int StackTraceMaxLength = 8000;
    private const int PathMaxLength = 200;
    private const int MethodMaxLength = 10;
    private const int TraceIdMaxLength = 50;

    private SystemError() { }

    // Enforces every column cap (trim, truncate, blank -> null) so an over-long value can never reach SQL.
    // path must be a bare request path — the caller is responsible for stripping any query string.
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

    // Trim then cap to the column length; a blank value stores null rather than an empty string.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }

    // As Cap, but for a required column: a blank value falls back to a fixed sentinel instead of null.
    private static string CapRequired(string? raw, int maxLength, string fallback) =>
        Cap(raw, maxLength) ?? fallback;
}
