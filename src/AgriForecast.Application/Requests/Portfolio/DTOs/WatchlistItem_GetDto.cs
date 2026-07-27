namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// One crop on the caller's watchlist. Returned by GET /api/portfolio/watchlist (as an array) and inside
// the POST response, so the UI can insert a new row without refetching the list.
public class WatchlistItem_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;

    // Display-only business code (VEG######/FRT######/DMB######). Never a join key — the ML side and every
    // FK key on the lowercase GUID CropId.
    public string? CropCode { get; set; }

    // The farmer's home market as stored on this row. Null means no market chosen, which the dashboard
    // reads as the national / economic-centre default — NOT missing data.
    public Guid? PreferredMarketId { get; set; }
    public string? PreferredMarketName { get; set; }

    // UTC instant, "Z"-stamped so the UI cannot read it as local time.
    public DateTime CreatedAtUtc { get; set; }
}
