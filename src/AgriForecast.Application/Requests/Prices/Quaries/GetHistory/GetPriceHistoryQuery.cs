using System.Collections.Generic;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Prices.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Prices.Quaries.GetHistory;

// GET /api/prices/crop/{cropId}/history?marketId=&days=. Observed daily low-high price
// series for one crop, optionally scoped to a single market. Feeds the farmer Prices page.
public class GetPriceHistoryQuery : IRequest<Result<List<PriceHistoryPoint_GetDto>>>
{
    public Guid CropId { get; set; }

    // Optional. When set, the series is that ONE market's history. When null, the series
    // is the cross-market daily envelope for the crop (min of all markets' lows, max of
    // all markets' highs per date) — see the handler note.
    public Guid? MarketId { get; set; }

    // Trailing window in days, anchored at the latest observation for the series. Clamped
    // to [7, 365] by the handler; default 90.
    public int Days { get; set; } = 90;
}
