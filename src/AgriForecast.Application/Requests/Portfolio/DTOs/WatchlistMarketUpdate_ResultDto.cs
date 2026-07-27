namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for PUT /api/portfolio/watchlist/{cropId}.
//
// The route names ONE crop, but the home market is a per-farmer setting: the update is applied to every
// crop the caller watches, in one transaction. AppliedToCropCount says so out loud, so the UI can tell the
// farmer "prices for all 5 of your crops now show Dambulla" instead of implying a per-crop change.
public class WatchlistMarketUpdate_ResultDto
{
    // The crop from the route — echoed back so the caller can correlate the response.
    public Guid CropId { get; set; }

    // The home market now in force. Null = no market chosen (national / economic-centre default).
    public Guid? PreferredMarketId { get; set; }
    public string? PreferredMarketName { get; set; }

    // How many of the caller's watchlist rows the value now applies to — i.e. all of them. Always >= 1
    // (the row named in the route), so it is never a confusing zero when the market was already set.
    public int AppliedToCropCount { get; set; }
}
