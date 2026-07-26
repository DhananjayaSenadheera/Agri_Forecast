using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Users.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Commands.UpdateRole;

/// <summary>
/// Admin-only change of a user's role. TargetUserId and Role come from the request body; ActingUserId is
/// stamped by the controller from the JWT sub claim and is never bound from the body. Fail-closed: only
/// assignable roles are accepted, and the last remaining admin cannot be demoted.
/// </summary>
public class UpdateUserRoleCommand : IRequest<Result<AdminUserDto>>
{
    public Guid TargetUserId { get; set; }
    public string Role { get; set; } = string.Empty;

    /// <summary>Set server-side from the authenticated admin's JWT sub claim. Not part of the wire body.</summary>
    public Guid ActingUserId { get; set; }
}
