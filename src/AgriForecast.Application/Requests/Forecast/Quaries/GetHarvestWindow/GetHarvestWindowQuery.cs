using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;

public class GetHarvestWindowQuery : IRequest<Result<HarvestWindowDto>>
{
    public Guid CropId { get; set; }
    public DateOnly? AsOf { get; set; }

    // How many days of candidate planting dates to sweep. 90 covers a full
    // planting decision without letting the frozen price/weather anchor go stale.
    public int HorizonDays { get; set; } = 90;
}
