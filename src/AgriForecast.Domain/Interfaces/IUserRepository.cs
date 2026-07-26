using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IUserRepository
{
    Task<User> AddAsync(User user);
    Task<User?> GetByIdAsync(Guid id);
    Task<User?> GetByUsernameAsync(string username);
    Task<User?> GetByEmailAsync(string email);
    Task<IEnumerable<User>> GetAllAsync();

    /// <summary>Persists a role or profile change on an existing user.</summary>
    Task UpdateAsync(User user);

    Task DeleteAsync(User user);

    /// <summary>
    /// Number of users holding the given role. Used by the last-admin guard so a delete or demote can
    /// never drop the Admin count to zero; read in the same request scope as the mutating write.
    /// </summary>
    Task<int> CountByRoleAsync(string role);
}
