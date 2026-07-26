using System.Collections.Generic;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Market.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Market.Quaries.GetAll;

// GET /api/markets/get/all. Returns all markets by default (the admin registry needs the full set). With
// HasPrices the result is limited to markets carrying at least one confirmed PriceObservation.
public class GetMarketsQuery : IRequest<Result<List<Market_GetDto>>>
{
    // ?hasPrices=true -> only markets with >=1 confirmed PriceObservation.
    public bool HasPrices { get; set; }
}
