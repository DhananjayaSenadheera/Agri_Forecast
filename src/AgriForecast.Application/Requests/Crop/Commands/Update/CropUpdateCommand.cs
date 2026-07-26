using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Commands.Update;

public class CropUpdateCommand : IRequest<Result<bool>> 
{
    public Crop_UpdateDto CropUpdateDto { get; set; } 

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
