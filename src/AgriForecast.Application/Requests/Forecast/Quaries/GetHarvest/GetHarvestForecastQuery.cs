using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;

public class GetHarvestForecastQuery : IRequest<Result<HarvestForecast_GetDto>>
{
    public Guid CropId { get; set; }
    public DateOnly PlantDate { get; set; }
}
