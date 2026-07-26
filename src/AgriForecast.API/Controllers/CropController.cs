using AgriForecast.API.Extensions;
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

// Reads are open to any authenticated user. Mutations are Admin-only: they change the crop dimension the
// ML model trains and serves on, so a farmer must not be able to touch them.
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
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpPut("update")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> Update([FromBody] CropUpdateCommand command)
    {
        // The acting admin comes from the JWT, never the request body, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));
        command.ActingUserId = actingId.Value;

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
            
        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpGet("get/{id}")]
    public async Task<IActionResult> GetCropById(Guid id)
    {
        var result = await mediator.Send(new CropGetByIdQuery(id));
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAllCrops()
    {
        var result = await mediator.Send(new CropGetAllQuery());
        if (result.IsSuccess)           
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
    [HttpDelete("delete/{id}")]
    [Authorize(Roles = UserRoles.Admin)]
    public async Task<IActionResult> DeleteCrop(Guid id)
    {
        // The acting admin comes from the JWT, never the route, so the audit row cannot be forged.
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new CropDeleteCommand(id, actingId.Value));
        if (result.IsSuccess)            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
    
   
}