using AgriForecast.Application.Requests.Auth.Commands.Login;
using AgriForecast.Application.Requests.Auth.Commands.Register;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
// Stricter rate limit on auth endpoints to blunt credential-stuffing / brute force (F-08).
[EnableRateLimiting("auth")]
public class AuthController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Auth", message = error }
        }
    };

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);

        return Unauthorized(ToErrorResponse(result.Error));
    }
}
