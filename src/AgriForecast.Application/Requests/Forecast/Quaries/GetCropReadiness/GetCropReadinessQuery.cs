using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetCropReadiness;

public class GetCropReadinessQuery : IRequest<Result<CropReadiness_GetDto>>
{
}
