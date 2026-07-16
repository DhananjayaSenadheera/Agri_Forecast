using System.Collections.Generic;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Market.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Market.Quaries.GetAll;

// GET /api/markets/get/all. Returns ALL markets by default (the admin registry needs
// the full set of 12). When HasPrices is true the result is filtered to markets that
// carry at least one CONFIRMED PriceObservation (the farmer Prices page only wants
// price-carrying markets — 10 today).
public class GetMarketsQuery : IRequest<Result<List<Market_GetDto>>>
{
    // ?hasPrices=true -> only markets with >=1 confirmed PriceObservation.
    public bool HasPrices { get; set; }
}
