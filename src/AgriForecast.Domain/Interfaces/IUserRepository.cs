using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IUserRepository
{
    Task<User> AddAsync(User user);
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByEmailAsync(string email);
    Task<IEnumerable<User>> GetAllAsync();

    /// <summary>Persists a role/profile change on an existing user (API-9 admin user-management).</summary>
    Task UpdateAsync(User user);

    /// <summary>Removes a user (API-9 admin user-management).</summary>
    Task DeleteAsync(User user);

    /// <summary>
    /// Number of users currently holding the given role. Used by the last-admin guard so a
    /// delete/demote can never drop the Admin count to zero. Read inside the same request scope
    /// as the mutating write so both observe the same DbContext.
    /// </summary>
    Task<int> CountByRoleAsync(string role);
}
