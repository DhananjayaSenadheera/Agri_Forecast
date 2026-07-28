using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;

namespace AgriForecast.Application.Requests.Portfolio.Common;

// The ONE projection from a stored watchlist row to the wire item. GET, POST and PUT all return the same
// shape, and they return it by calling this — three hand-written copies would drift the day one of them
// gained a field.
internal static class WatchlistItemMapper
{
    public static WatchlistItem_GetDto ToDto(WatchlistRow row) => new()
    {
        CropId = row.CropId,
        CropName = row.CropName,
        CropCode = row.CropCode,
        PlantedDate = PortfolioTime.Fmt(row.PlantedDate),
        Markets = row.Markets
            .Select(m => new WatchlistMarket_GetDto
            {
                MarketId = m.MarketId,
                Name = m.Name,
                ShortCode = m.ShortCode
            })
            .ToList(),
        CreatedAtUtc = PortfolioTime.AsUtc(row.CreatedAtUtc)
    };
}
