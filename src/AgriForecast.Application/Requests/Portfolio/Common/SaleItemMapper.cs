using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;

namespace AgriForecast.Application.Requests.Portfolio.Common;

// The ONE projection from a stored sale to its wire shape, shared by the list, the POST response and the
// PUT response. Precedent: WatchlistItemMapper — three endpoints rendering the same row three ways is how a
// UI ends up with a field that only exists after a refresh.
internal static class SaleItemMapper
{
    public static SaleItem_GetDto ToDto(UserSaleRow row) => new()
    {
        Id = row.Id,
        CropId = row.CropId,
        CropName = row.CropName,
        CropCode = row.CropCode,
        MarketId = row.MarketId,
        MarketName = row.MarketName,
        MarketShortCode = row.MarketShortCode,
        SaleDate = PortfolioTime.Fmt(row.SaleDate),
        PricePerKg = row.PricePerKg,
        QuantityKg = row.QuantityKg,
        Note = row.Note,
        CreatedAtUtc = PortfolioTime.AsUtc(row.CreatedAtUtc),
        UpdatedAtUtc = PortfolioTime.AsUtc(row.UpdatedAtUtc)
    };
}
