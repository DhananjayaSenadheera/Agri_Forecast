using AgriForecast.Application.Requests.EcconomicCenter.Commands.Create;
using AgriForecast.Application.Requests.EcconomicCenter.Commands.Delete;
using AgriForecast.Application.Requests.EcconomicCenter.Commands.Update;
using AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetAll;
using AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetOneById;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/eco")]
[Authorize]
public class EcoControler(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Economic Center", message = error }
        }
    };
    
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] EcoCreateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
            //return StatusCode(statusCode: StatusCodes.Status201Created);
        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpDelete("delete/{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var result = await mediator.Send(new EcoDeleteCommand(id));
        if (result.IsSuccess)
            return Ok();
        
        return BadRequest(ToErrorResponse(result.Error));
    }
    
    [HttpPut("update")]
    public async Task<IActionResult> Update([FromBody] EcoUpdateCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok();
        
        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpGet("get/{id}")]
    public async Task<IActionResult> GetById(Guid id)
    {
        var result = await mediator.Send(new EcoGetByIdQuery(id));
        if (result.IsSuccess) 
            return Ok(result.Data);
        
        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll()
    {
        var result = await mediator.Send(new EcoGetAllQuery());
        if (result.IsSuccess) 
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

}