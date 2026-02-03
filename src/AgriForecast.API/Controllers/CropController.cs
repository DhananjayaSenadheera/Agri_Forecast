using AgriForecast.Application.Requests.Crop.Commands.Create;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/crops")]
public class CropController(IMediator mediator) : ControllerBase
{
    [HttpPost("create")]
    public async Task<IActionResult> CreateCrop([FromBody] CropCreateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return StatusCode(statusCode: StatusCodes.Status201Created);

        return BadRequest(ToErrorResponse(result.Error));
    }
    
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Crop", message = error }
        }
    };
}