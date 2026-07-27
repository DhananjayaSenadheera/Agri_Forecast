namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for DELETE /api/portfolio/watchlist/{cropId}. A crop that was not on the caller's watchlist is
// a 404, so a 200 here always means a row really was removed.
public class WatchlistRemove_ResultDto
{
    public Guid CropId { get; set; }
    public bool Removed { get; set; }
}
