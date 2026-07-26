using AgriForecast.API.Extensions;
using AgriForecast.Application.Requests.Users.Commands.Create;
using AgriForecast.Application.Requests.Users.Commands.Delete;
using AgriForecast.Application.Requests.Users.Commands.UpdateRole;
using AgriForecast.Application.Requests.Users.DTOs;
using AgriForecast.Application.Requests.Users.Quaries.GetAll;
using AgriForecast.Domain.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AgriForecast.API.Controllers;

// Admin-only user management: the whole controller is locked to the Admin role, so an authenticated Farmer
// gets a 403 and an unauthenticated caller a 401. The acting admin's identity is always read from the JWT
// sub claim — never the body or route — so a caller cannot spoof "who am I" past the self-delete guard.
[ApiController]
[Route("api/users")]
[Authorize(Roles = UserRoles.Admin)]
public class UserController(IMediator mediator) : ControllerBase
{
    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "User", message = error }
        }
    };

    // The acting admin's immutable user id comes from the JWT subject via the shared
    // ActingUserExtensions.GetActingUserId(), so the call sites cannot drift onto the wrong claim.

    // GET /api/users/get/all?page=&pageSize= — paging is optional, and the defaults return every user in one
    // generous page because the admin console paginates client-side.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 500)
    {
        var result = await mediator.Send(new GetAllUsersQuery { Page = page, PageSize = pageSize });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/users/create — Admin-only provisioning of an account. Distinct from the anonymous
    // POST /api/auth/register, which issues a refresh cookie to the CALLER and would therefore replace the
    // acting admin's own cookie. This issues no token and no cookie, only the AdminUserDto projection.
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateUserDto body)
    {
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var command = new CreateUserCommand
        {
            Username = body.Username,
            Email = body.Email,
            Password = body.Password,
            Role = body.Role,
            ActingUserId = actingId.Value
        };

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // PUT /api/users/update-role  body: { userId, role }
    // Whitelist ("Admin" | "Farmer") + last-admin-demote guard enforced downstream.
    [HttpPut("update-role")]
    public async Task<IActionResult> UpdateRole([FromBody] UpdateUserRoleDto body)
    {
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var command = new UpdateUserRoleCommand
        {
            TargetUserId = body.UserId,
            Role = body.Role,
            ActingUserId = actingId.Value
        };

        var result = await mediator.Send(command);
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // DELETE /api/users/delete/{id}
    // Self-delete + last-admin-delete guards enforced downstream. The acting id comes from the JWT,
    // never the route, so it cannot be forged to bypass the self-delete guard.
    [HttpDelete("delete/{id}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var actingId = this.GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new DeleteUserCommand(id, actingId.Value));
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
