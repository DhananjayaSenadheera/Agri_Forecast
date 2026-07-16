using AgriForecast.Application.Requests.Crop.Commands.Create;
using AgriForecast.Application.Requests.Crop.Commands.Delete;
using AgriForecast.Application.Requests.Crop.Commands.Update;
using AgriForecast.Application.Requests.Crop.Quaries.GetAll;
using AgriForecast.Application.Requests.Crop.Quaries.GetOneById;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Reads are open to any authenticated user ([Authorize] at class level). Mutations (create/update/
// delete) are Admin-only (API-9): they change the crop dimension the ML model trains/serves on, so
// a farmer must not be able to touch them. Method-level [Authorize(Roles = "Admin")] overrides the
// class-level policy for those actions.
[ApiController]
[Route("api/crops")]
[Authorize]
public class CropController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Crop", message = error }
        }
    };
    
    [HttpPost("create")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> CreateCrop([FromBody] CropCreateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
            //return StatusCode(statusCode: StatusCodes.Status201Created);

        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
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
    
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAllCrops()
    {
        var result = await mediator.Send(new CropGetAllQuery());
        if (result.IsSuccess)           
            return Ok(result.Data);
            //return Task.FromResult<IActionResult>(StatusCode(statusCode: StatusCodes.Status204NoContent));
        return BadRequest(ToErrorResponse(result.Error));
    }
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> DeleteCrop(Guid id)
    {
        var result = await mediator.Send(new CropDeleteCommand(id));
        if (result.IsSuccess)            return Ok(result.Data);
            //return Task.FromResult<IActionResult>(StatusCode(statusCode: StatusCodes.Status204NoContent));
        return BadRequest(ToErrorResponse(result.Error));
    }
    
   
}