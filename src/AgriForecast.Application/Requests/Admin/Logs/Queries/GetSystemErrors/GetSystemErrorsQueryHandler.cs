using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// Server-paged system-error history. Pages via ILogsReadStore and maps each row to the DTO by hand.
public class GetSystemErrorsQueryHandler
    : IRequestHandler<GetSystemErrorsQuery, Result<SystemErrorsPage_GetDto>>
{
    private readonly ILogsReadStore _store;

    public GetSystemErrorsQueryHandler(ILogsReadStore store) => _store = store;

    public async Task<Result<SystemErrorsPage_GetDto>> Handle(
        GetSystemErrorsQuery request, CancellationToken cancellationToken)
    {
        var page = await _store.GetSystemErrorsAsync(request.Page, request.PageSize, cancellationToken);

        var items = page.Items
            .Select(r => new SystemError_GetDto
            {
                Id = r.Id,
                OccurredUtc = AsUtc(r.OccurredUtc),
                Source = r.Source,
                ExceptionType = r.ExceptionType,
                Message = r.Message,
                Path = r.Path,
                Method = r.Method,
                TraceId = r.TraceId,
                StackTrace = r.StackTrace
            })
            .ToList();

        var dto = new SystemErrorsPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<SystemErrorsPage_GetDto>.Success(dto);
    }

    // EF materializes datetime2 as DateTimeKind.Unspecified, so stamp Kind=Utc here for the trailing "Z"
    // — the same local fix the other Logs handlers use.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
