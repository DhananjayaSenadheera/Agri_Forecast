using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Commands.Create;

public class CropCreateCommand : IRequest<Result<bool>>
{
    public CropCreateCommand(Crop_CreateDto dto)
    {
        CreateDto = dto ?? throw new ArgumentNullException(nameof(dto));
        
    }
    public Crop_CreateDto CreateDto { get; set; }
}