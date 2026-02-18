using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Commands.Delete;

public class CropDeleteCommand : IRequest<Result<bool>>
{
    public CropDeleteCommand(Guid cropId)
    {
        Id = cropId;
    }

    public Guid Id { get; set; }
}