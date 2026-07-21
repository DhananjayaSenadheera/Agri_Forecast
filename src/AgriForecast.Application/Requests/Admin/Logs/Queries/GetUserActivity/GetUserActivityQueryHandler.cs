using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// Server-paged account-event history. Validation (bounds + known type) already ran in the pipeline,
// so this handler parses the optional type filter to the enum, pages via the store, and maps each row
// to the DTO (enum -> frozen wire string, UTC-kind stamp). The DB is behind ILogsReadStore for
// unit-testability. Mirrors GetIngestionRunsQueryHandler (manual mapping — no AutoMapper in the house).
public class GetUserActivityQueryHandler
    : IRequestHandler<GetUserActivityQuery, Result<UserActivityPage_GetDto>>
{
    private readonly ILogsReadStore _store;

    public GetUserActivityQueryHandler(ILogsReadStore store) => _store = store;

    public async Task<Result<UserActivityPage_GetDto>> Handle(
        GetUserActivityQuery request, CancellationToken cancellationToken)
    {
        // Null when blank; the validator already rejected an unknown non-blank type.
        var type = UserActivityEventStrings.TryParse(request.Type);

        var page = await _store.GetUserActivityPageAsync(
            request.Page, request.PageSize, type, cancellationToken);

        var items = page.Items
            .Select(e => new UserActivity_GetDto
            {
                OccurredUtc = AsUtc(e.OccurredUtc),
                EventType = UserActivityEventStrings.ToWire(e.EventType),
                ActorUserId = e.ActorUserId,
                TargetUserId = e.TargetUserId,
                UsernameAttempted = e.UsernameAttempted,
                Details = e.Details
            })
            .ToList();

        var dto = new UserActivityPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<UserActivityPage_GetDto>.Success(dto);
    }

    // EF materializes datetime2 as DateTimeKind.Unspecified — stamp Kind=Utc so System.Text.Json
    // emits the trailing "Z" and the FE reads the instant as UTC (same as the ingestion reads).
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
