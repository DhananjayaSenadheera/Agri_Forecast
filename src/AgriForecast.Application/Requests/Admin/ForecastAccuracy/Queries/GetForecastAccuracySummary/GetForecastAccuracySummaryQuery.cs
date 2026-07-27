using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// GET /api/admin/forecast-accuracy/summary. How the served forecasts have actually scored: state counts
// for the whole ledger, and the accuracy aggregates — over a bounded window — split by active predictor
// and by model version. Admin-only and read-only; the window bound is enforced by
// GetForecastAccuracySummaryValidator.
public class GetForecastAccuracySummaryQuery : IRequest<Result<ForecastAccuracySummary_GetDto>>
{
    // A year of snapshots: long enough to span a full Yala/Maha cycle, so a seasonal weakness cannot sit
    // just outside the window, and short enough that the summary never materialises the whole ledger.
    public const int DefaultWindowDays = 365;

    // Aggregates cover matured rows with SnapshotDate >= today − WindowDays. The window is echoed back
    // on the response, so a metric can never be read without knowing what it spans. The counts block
    // deliberately ignores it and stays all-time: it is a census of the ledger, not a skill measure.
    public int WindowDays { get; set; } = DefaultWindowDays;
}
