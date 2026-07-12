using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetMarketOverview;

public class GetMarketOverviewQuery : IRequest<Result<MarketOverview_GetDto>>
{
    // Trailing window in days. Clamped to [7, 90] by the handler; default 30.
    public int Days { get; set; } = 30;
}
