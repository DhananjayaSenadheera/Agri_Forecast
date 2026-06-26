using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;

public class GetCropTimelineQuery : IRequest<Result<CropTimelineDto>>
{
    public Guid CropId { get; set; }
    public DateOnly? AsOf { get; set; }
    public int Months { get; set; } = 12;
}
