namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// Response for PUT /api/portfolio/watchlist/{cropId}.
//
// The whole updated entry comes back, in the SAME shape GET /watchlist returns, so the UI replaces one row
// with the server's version instead of patching its local copy from a partial response and hoping the two
// agree. The change flags are for messaging only ("markets updated" vs "nothing changed"); they are never
// the source of truth for what the entry now holds — Item is.
public class WatchlistEntryUpdate_ResultDto
{
    public WatchlistItem_GetDto Item { get; set; } = new();

    // True when the watched-market set actually differs from what was stored before this call. False for a
    // no-op replace (the same markets sent again) and for a request that omitted marketIds entirely.
    public bool MarketsChanged { get; set; }

    // True when the planting date actually changed (set, cleared or moved). False for an omitted field or
    // a re-send of the same date.
    public bool PlantedDateChanged { get; set; }
}
