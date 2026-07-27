using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// GET /api/admin/forecast-accuracy/summary. How the served forecasts have actually scored so far:
// state counts, and the accuracy aggregates split by active predictor and by model version. Admin-only
// and read-only. Deliberately parameterless — the whole matured history is the honest denominator, and
// a window parameter would let a bad month be tuned out of view. Narrowing lives on /snapshots.
public class GetForecastAccuracySummaryQuery : IRequest<Result<ForecastAccuracySummary_GetDto>>
{
}
