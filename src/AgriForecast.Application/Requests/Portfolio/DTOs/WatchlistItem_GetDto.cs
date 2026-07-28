namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// One crop on the caller's watchlist. Returned by GET /api/portfolio/watchlist (as an array), inside the
// POST response and as the PUT response, so the UI can update a row without refetching the list.
public class WatchlistItem_GetDto
{
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;

    // Display-only business code (VEG######/FRT######/DMB######). Never a join key — the ML side and every
    // FK key on the lowercase GUID CropId.
    public string? CropCode { get; set; }

    // The farmer's own planting day for this crop, yyyy-MM-dd, or null when they have not recorded one.
    // A date STRING rather than a DateTime: it has no time component and no timezone, and shipping it as
    // an instant is how a planting day becomes "the day before" for half the world.
    public string? PlantedDate { get; set; }

    // The markets this crop is watched at, oldest-chosen first (a stable order the UI can render without
    // re-sorting). EMPTY is a normal state: no market chosen means the national / economic-centre default,
    // not missing data.
    public List<WatchlistMarket_GetDto> Markets { get; set; } = new();

    // UTC instant, "Z"-stamped so the UI cannot read it as local time.
    public DateTime CreatedAtUtc { get; set; }
}

// One watched market, as the farmer UI shows it.
public class WatchlistMarket_GetDto
{
    public Guid MarketId { get; set; }
    public string Name { get; set; } = string.Empty;

    // The short chip label (e.g. "DEC", "KEP"). Display-only and possibly empty — never a key. The FE must
    // keep addressing markets by marketId.
    public string ShortCode { get; set; } = string.Empty;
}
