namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// Response envelope for GET /api/admin/logs/user-activity. An empty page is a 200 with an empty Items
// list, never a 404.
public class UserActivityPage_GetDto
{
    public List<UserActivity_GetDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int Total { get; set; }
}
