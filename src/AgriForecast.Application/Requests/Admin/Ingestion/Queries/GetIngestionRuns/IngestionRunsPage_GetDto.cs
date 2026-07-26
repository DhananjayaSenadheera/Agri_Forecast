namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// Response envelope for GET /api/admin/ingestion/runs. An empty page is a 200 with an empty Items list,
// never a 404.
public class IngestionRunsPage_GetDto
{
    public List<IngestionRun_GetDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}
