using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetOneById;

public class CropGetByIdQuery : IRequest<Result<Crop_GetDto>>
{
    public CropGetByIdQuery(Guid id)
    {
        Guid = id;
    }

    public Guid Guid { get; set; }
}