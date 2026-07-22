namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// One system error for GET /api/admin/logs/errors. Only the exception type/message/stack and the
// request method/path (path ONLY — never a query string) plus the trace id are surfaced; no request
// field (query string, header, body) is captured directly, but message/stackTrace are the verbatim
// (length-capped) exception text and may carry whatever upstream code put in them — Admin-only by the
// controller. message/path/method/traceId/stackTrace are nullable (blank -> null at write time).
// PascalCase -> camelCase JSON per the API default policy.
public class SystemError_GetDto
{
    public long Id { get; set; }
    public DateTime OccurredUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public string ExceptionType { get; set; } = string.Empty;
    public string? Message { get; set; }
    public string? Path { get; set; }
    public string? Method { get; set; }
    public string? TraceId { get; set; }
    public string? StackTrace { get; set; }
}
