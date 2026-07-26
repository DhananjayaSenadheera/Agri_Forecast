using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// GET /api/admin/logs/training. Server-paged model-training history, newest TrainedAtUtc first (Id DESC
// tiebreak). Admin-only and read-only; the bounds are enforced by GetTrainingRunsValidator.
public class GetTrainingRunsQuery : IRequest<Result<TrainingRunsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
