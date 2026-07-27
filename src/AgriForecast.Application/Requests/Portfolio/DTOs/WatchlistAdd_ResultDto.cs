namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for POST /api/portfolio/watchlist.
//
// Adding a crop is IDEMPOTENT, not a create: "watch this crop" is set membership, and a farmer who
// double-taps on a slow mobile connection must not be shown an error for a state they already have. So a
// repeat add is a 200 with AlreadyPresent = true and the existing row, not a 409.
public class WatchlistAdd_ResultDto
{
    public WatchlistItem_GetDto Item { get; set; } = new();

    // True when the crop was already on the watchlist and no row was created. The UI can use it to skip a
    // "added" toast; it is not an error signal.
    public bool AlreadyPresent { get; set; }
}
