namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// Request body for PUT /api/users/update-role. Carries only the target user id and the new role — the
/// acting admin's identity comes from the JWT sub claim, never the body.
/// </summary>
public class UpdateUserRoleDto
{
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
}
