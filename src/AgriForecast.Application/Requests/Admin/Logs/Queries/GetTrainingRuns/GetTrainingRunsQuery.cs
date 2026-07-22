using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// GET /api/admin/logs/training?page=1&pageSize=20. Server-paged model-training history, newest
// TrainedAtUtc first (Id DESC tiebreak). Admin-only (controller enforces the role); read-only.
// Bounds are enforced by GetTrainingRunsValidator (bad values -> 400 via the validation pipeline).
public class GetTrainingRunsQuery : IRequest<Result<TrainingRunsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
