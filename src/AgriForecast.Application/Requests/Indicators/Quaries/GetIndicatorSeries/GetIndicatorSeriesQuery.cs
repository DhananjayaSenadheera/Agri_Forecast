using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetIndicatorSeries;

// GET /api/indicators?code=&from=&to=. Daily EconomicIndicators readings (e.g. USD_LKR) for
// one series. Read-only, feeds the admin Indicators page. from/to are inclusive and optional;
// when both absent the handler defaults to the last 365 days ending at the series' latest date.
public class GetIndicatorSeriesQuery : IRequest<Result<List<DailyIndicatorPoint_GetDto>>>
{
    // Required. The IndicatorCode to fetch (e.g. "USD_LKR"). Blank => validation failure.
    public string? Code { get; set; }

    // Optional inclusive window bounds on Date. See handler for default-window resolution.
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}
