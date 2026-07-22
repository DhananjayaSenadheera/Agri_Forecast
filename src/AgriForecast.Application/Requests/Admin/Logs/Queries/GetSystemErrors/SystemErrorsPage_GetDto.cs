namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// Response envelope for GET /api/admin/logs/errors — a server-paged list newest-first. An empty page is
// a 200 with an empty Items list (house convention), never a 404. Mirrors the training/user-activity
// envelope shape (Items/Page/PageSize/Total).
public class SystemErrorsPage_GetDto
{
    public List<SystemError_GetDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}
