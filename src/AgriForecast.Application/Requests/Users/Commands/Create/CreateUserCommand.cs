using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Users.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Commands.Create;

/// <summary>
/// Admin-only creation of a user account. Deliberately separate from the anonymous RegisterCommand,
/// which always writes Role = "Farmer" and returns an AuthResponseDto whose refresh cookie the controller
/// issues to the CALLER — reusing it from the admin console would overwrite the acting admin's own cookie
/// and take over their session. This command issues no token and no cookie.
/// </summary>
public class CreateUserCommand : IRequest<Result<AdminUserDto>>
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;

    /// <summary>Set server-side from the authenticated admin's JWT sub claim. Not part of the wire body.</summary>
    public Guid ActingUserId { get; set; }
}
