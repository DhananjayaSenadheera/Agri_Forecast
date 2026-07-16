using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Infrastructure.Repositories;

public class UserRepository : IUserRepository
{
    private readonly IGenericRepository<User> _userRepository;

    public UserRepository(IGenericRepository<User> userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<User> AddAsync(User user)
    {
        await _userRepository.AddAsync(user);
        return user;
    }

    public async Task<User?> GetByIdAsync(Guid id)
    {
        return await _userRepository.GetByIdAsync(id);
    }

    public async Task<User?> GetByUsernameAsync(string username)
    {
        return await _userRepository.GetOneAsyncInclude(u => u.Username == username);
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _userRepository.GetOneAsyncInclude(u => u.Email == email);
    }

    public async Task<IEnumerable<User>> GetAllAsync()
    {
        return await _userRepository.GetAllAsync();
    }

    public async Task UpdateAsync(User user)
    {
        await _userRepository.UpdateAsync(user);
    }

    public async Task DeleteAsync(User user)
    {
        await _userRepository.DeleteAsync(user);
    }

    public async Task<int> CountByRoleAsync(string role)
    {
        // The users table is tiny (admins + registered farmers), so an in-memory count over the
        // scoped DbContext read is cheap and keeps this on the existing IGenericRepository surface.
        // Case-insensitive so a "admin"/"Admin" data drift can never bypass the last-admin guard.
        var all = await _userRepository.GetAllAsync();
        return all.Count(u => string.Equals(u.Role, role, StringComparison.OrdinalIgnoreCase));
    }
}
