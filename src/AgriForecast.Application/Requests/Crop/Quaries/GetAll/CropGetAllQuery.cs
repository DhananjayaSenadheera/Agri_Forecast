using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetAll;

public class CropGetAllQuery : IRequest<Result<List<Crop_GetDto>>>
{
    
}