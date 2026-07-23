using System.Security.Claims;
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

// API-9 — Admin-only user management. The WHOLE controller is locked to the Admin role: an
// authenticated Farmer receives 403, an unauthenticated caller 401 (both enforced by the
// authorization middleware from the JWT role claim). The acting admin's identity is ALWAYS read
// from the JWT sub claim — never from the request body/route — so a caller cannot spoof "who am I"
// to slip past the self-delete guard.
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

    // Resolves the authenticated admin's immutable user id from the JWT subject. Default inbound
    // claim mapping turns "sub" into ClaimTypes.NameIdentifier; the token generator also sets
    // NameIdentifier explicitly, so this is stable. Returns null if the claim is missing/malformed.
    private Guid? GetActingUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
        return Guid.TryParse(sub, out var id) ? id : null;
    }

    // GET /api/users/get/all?page=&pageSize=
    // Paging is optional; defaults return every user in one generous page so the frontend admin
    // console (which paginates client-side over a flat AdminUser[]) works unchanged.
    [HttpGet("get/all")]
    public async Task<IActionResult> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 500)
    {
        var result = await mediator.Send(new GetAllUsersQuery { Page = page, PageSize = pageSize });
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }

    // POST /api/users/create  body: { username, email, password, role }
    // Admin-only provisioning of an account. Distinct from the anonymous POST /api/auth/register:
    // that path issues a refresh cookie to the CALLER, which here would replace the acting admin's
    // own cookie with the new user's. This one issues no token and no cookie — it returns the
    // AdminUserDto projection only. The acting id comes from the JWT, never the body.
    [HttpPost("create")]
    public async Task<IActionResult> Create([FromBody] CreateUserDto body)
    {
        var actingId = GetActingUserId();
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
        var actingId = GetActingUserId();
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
        var actingId = GetActingUserId();
        if (actingId is null)
            return Unauthorized(ToErrorResponse("Unable to identify the acting user."));

        var result = await mediator.Send(new DeleteUserCommand(id, actingId.Value));
        if (result.IsSuccess)
            return Ok(result.Data);
        return BadRequest(ToErrorResponse(result.Error));
    }
}
