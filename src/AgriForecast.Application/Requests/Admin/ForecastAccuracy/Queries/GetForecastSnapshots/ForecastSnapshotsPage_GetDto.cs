namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;

// Response envelope for GET /api/admin/forecast-accuracy/snapshots. An empty page — including a filter
// that matches nothing and a page past the end — is a 200 with an empty Items list, never a 404.
public class ForecastSnapshotsPage_GetDto
{
    public List<ForecastSnapshot_GetDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}
