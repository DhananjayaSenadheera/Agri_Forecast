namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// Request body for <c>POST /api/users/create</c> (Admin-only provisioning of an account from the
/// admin console). Mirrors the public <c>RegisterDto</c> fields and adds <see cref="Role"/>, which
/// self-registration does not have — a self-registered account is always a Farmer, whereas an admin
/// provisioning an account chooses from the assignable role whitelist.
/// <para>
/// The ACTING admin's identity is never taken from this body; the controller reads it from the JWT
/// <c>sub</c> claim and threads it onto the command server-side (same discipline as
/// <see cref="UpdateUserRoleDto"/>).
/// </para>
/// </summary>
public class CreateUserDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// The initial password, chosen by the admin and passed to the user out-of-band. Hashed by the
    /// handler before it reaches the data layer and NEVER echoed back, logged, or audited.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>One of <c>UserRoles.Assignable</c> ("Admin" | "Farmer"), exact case.</summary>
    public string Role { get; set; } = string.Empty;
}
