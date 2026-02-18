using AgriForecast.Application.Requests.Crop.Commands.Create;
using AgriForecast.Application.Requests.Crop.Commands.Update;
using AgriForecast.Application.Requests.Crop.Quaries.GetOneById;
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
            return Ok(result.Data);
            //return StatusCode(statusCode: StatusCodes.Status201Created);

        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpPut("update")]
    public async Task<IActionResult> Update([FromBody] CropUpdateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
            //return Task.FromResult<IActionResult>(StatusCode(statusCode: StatusCodes.Status204NoContent));
            
        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpGet("get/{id}")]
    public async Task<IActionResult> GetCropById(Guid id)
    {
        var result = await mediator.Send(new CropGetByIdQuery(id));
        if (result.IsSuccess)
            return Ok(result.Data);
            //return Task.FromResult<IActionResult>(StatusCode(statusCode: StatusCodes.Status204NoContent));
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