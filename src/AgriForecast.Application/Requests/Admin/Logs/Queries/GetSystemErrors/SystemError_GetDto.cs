namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// One system error for GET /api/admin/logs/errors. Only the exception type/message/stack and the request
// method and PATH (path only, never a query string) plus the trace id are surfaced. message and
// stackTrace are verbatim exception text, which is why the endpoint is Admin-only.
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
