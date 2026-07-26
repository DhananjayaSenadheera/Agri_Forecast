using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// Server-paged model-training history. Pages via ILogsReadStore and maps each row to the DTO by hand.
public class GetTrainingRunsQueryHandler
    : IRequestHandler<GetTrainingRunsQuery, Result<TrainingRunsPage_GetDto>>
{
    private readonly ILogsReadStore _store;

    public GetTrainingRunsQueryHandler(ILogsReadStore store) => _store = store;

    public async Task<Result<TrainingRunsPage_GetDto>> Handle(
        GetTrainingRunsQuery request, CancellationToken cancellationToken)
    {
        var page = await _store.GetTrainingRunsPageAsync(request.Page, request.PageSize, cancellationToken);

        var items = page.Items
            .Select(r => new TrainingRun_GetDto
            {
                Version = r.Version,
                TrainedAtUtc = AsUtc(r.TrainedAtUtc),
                Promoted = r.Promoted,
                DecisionPromoted = r.DecisionPromoted,
                PromotionDecision = r.PromotionDecision,
                BestMlKind = r.BestMlKind,
                BestMlMae = r.BestMlMae,
                BestBaselineKind = r.BestBaselineKind,
                BestBaselineMae = r.BestBaselineMae,
                NTrainRows = r.NTrainRows,
                NCrops = r.NCrops
            })
            .ToList();

        var dto = new TrainingRunsPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<TrainingRunsPage_GetDto>.Success(dto);
    }

    // EF materializes datetime2 as DateTimeKind.Unspecified, so stamp Kind=Utc here for the trailing "Z".
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
