namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// The user projection returned by the Admin-only user-management endpoints (API-9). Mirrors the
/// frontend <c>AdminUser</c> fixture contract exactly (camelCase on the wire):
/// <c>{ id, username, email, role, createdAt, updatedAt }</c>. Deliberately OMITS
/// <c>PasswordHash</c> — the hash never leaves the data layer.
/// </summary>
public class AdminUserDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
