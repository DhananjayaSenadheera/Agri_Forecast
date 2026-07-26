namespace AgriForecast.Application.Requests.Users.DTOs;

/// <summary>
/// The user projection returned by the Admin-only user-management endpoints. Deliberately omits
/// PasswordHash — the hash never leaves the data layer.
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
