namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// Request body for <c>PUT /api/users/update-role</c>. Carries ONLY the target user id and the new
/// role — the ACTING admin's identity is never taken from the body; the controller reads it from the
/// JWT <c>sub</c> claim and threads it onto the command server-side.
/// </summary>
public class UpdateUserRoleDto
{
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
}
