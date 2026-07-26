using AgriForecast.Application.Requests.Users.DTOs;
using UserEntity = AgriForecast.Domain.Entities.User;

namespace AgriForecast.Application.Mapper;

// User -> AdminUserDto for the Admin-only user-management endpoints. Deliberately does not copy
// PasswordHash — the hash never leaves the data layer.
public static class AdminUserMapper
{
    public static AdminUserDto ToAdminDto(this UserEntity src) => new()
    {
        Id = src.Id,
        Username = src.Username,
        Email = src.Email,
        Role = src.Role,
        CreatedAt = src.CreatedAt,
        UpdatedAt = src.UpdatedAt
    };
}
