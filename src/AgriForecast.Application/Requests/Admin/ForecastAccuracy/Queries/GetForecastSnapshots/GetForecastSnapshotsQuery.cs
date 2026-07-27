using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;

// GET /api/admin/forecast-accuracy/snapshots. The row-level ledger behind the summary: every frozen
// prediction with, once matured, the actual it scored against. Newest snapshot date first. Admin-only
// and read-only; bounds are enforced by GetForecastSnapshotsValidator.
public class GetForecastSnapshotsQuery : IRequest<Result<ForecastSnapshotsPage_GetDto>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;

    // Optional filters, AND-combined. Null (or blank, for the string) means no filter.
    public Guid? CropId { get; set; }
    public string? ModelVersion { get; set; }

    // true narrows to the MATURED state only — the scored rows. It is not "everything terminal":
    // actual_unavailable and not_maturable rows are terminal too and carry no error columns.
    public bool MaturedOnly { get; set; }
}
