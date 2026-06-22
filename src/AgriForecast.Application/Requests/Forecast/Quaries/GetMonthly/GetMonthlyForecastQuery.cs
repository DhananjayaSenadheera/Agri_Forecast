using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetMonthly;

public class GetMonthlyForecastQuery : IRequest<Result<List<MonthlyForecast_GetDto>>>
{
    public Guid CropId { get; set; }
    public int Months { get; set; } = 12;
}
