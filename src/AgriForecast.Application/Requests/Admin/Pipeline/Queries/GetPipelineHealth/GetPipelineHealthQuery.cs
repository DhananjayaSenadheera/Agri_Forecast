using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;

// GET /api/admin/pipeline/health. No inputs: the night being reported on is derived from the clock and
// the CronJob schedule, never supplied by the caller, so two admins always see the same answer.
public class GetPipelineHealthQuery : IRequest<Result<PipelineHealth_GetDto>>
{
}
