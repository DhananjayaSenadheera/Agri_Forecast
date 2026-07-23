using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Users.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Commands.Create;

/// <summary>
/// Admin-only creation of a user account. Deliberately SEPARATE from
/// <c>Auth.Commands.Register.RegisterCommand</c>, which is the anonymous self-registration path:
/// that one always writes Role = "Farmer" AND returns an <c>AuthResponseDto</c> whose refresh cookie
/// the controller issues to the CALLER — so reusing it from the admin console would overwrite the
/// acting admin's own refresh cookie with the new user's, silently taking over their session at the
/// next token refresh. This command issues NO token and NO cookie; it only writes the row and
/// returns the same <see cref="AdminUserDto"/> projection the rest of the admin user endpoints use.
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
