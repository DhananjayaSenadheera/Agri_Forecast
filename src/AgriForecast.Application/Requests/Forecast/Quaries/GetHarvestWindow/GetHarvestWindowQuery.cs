using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;

public class GetHarvestWindowQuery : IRequest<Result<HarvestWindowDto>>
{
    public Guid CropId { get; set; }
    public DateOnly? AsOf { get; set; }

    // Days of candidate planting dates to sweep. 90 covers a decision without the frozen price/weather
    // anchor going stale.
    public int HorizonDays { get; set; } = 90;
}
