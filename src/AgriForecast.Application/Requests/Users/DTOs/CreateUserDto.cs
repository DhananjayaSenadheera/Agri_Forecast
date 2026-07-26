namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// Request body for POST /api/users/create (Admin-only provisioning). Mirrors RegisterDto and adds Role,
/// which self-registration does not have: a self-registered account is always a Farmer.
/// The acting admin's identity is never taken from this body — the controller reads it from the JWT sub
/// claim and threads it onto the command.
/// </summary>
public class CreateUserDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>The initial password, hashed by the handler and never echoed back, logged, or audited.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>One of <c>UserRoles.Assignable</c> ("Admin" | "Farmer"), exact case.</summary>
    public string Role { get; set; } = string.Empty;
}
