using System.Linq.Expressions;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class GenericRepository<T>(AgriForecastDbContext dbContext) : IGenericRepository<T>
    where T : class
{
    private readonly DbSet<T> _dbSet = dbContext.Set<T>();
    
    public async Task AddAsync(T entity)
    {
        await _dbSet.AddAsync(entity);
    }

    public Task UpdateAsync(T entity)
    {
        _dbSet.Update(entity);
        return Task.FromResult(true);
    }

    public Task DeleteAsync(T entity)
    {
        _dbSet.Remove(entity);
        return Task.FromResult(true);
    }

    public async Task<T> GetoneAsync()
    {
       var result = await _dbSet.AsNoTracking().FirstOrDefaultAsync();
        return result;
    }

    public async Task<T> GetByIdAsync(Guid id)
    {
        var result = await _dbSet.FindAsync(id);
        return result;
    }

    public async Task<T> GetByIdAsync(int id)
    {
        var result = await _dbSet.FindAsync(id);
        return result;
    }

    public async Task<T> GetByCodeAsync(string ecoCode)
    {
        var result = await _dbSet.FindAsync(ecoCode);
        return result;
    }

    public async Task<T?> GetOneAsyncInclude(Expression<Func<T, bool>> predicate)
    {
        var result = await _dbSet.AsNoTracking().FirstOrDefaultAsync(predicate);
        return result;
    }

    public async Task<IEnumerable<T>> GetAllAsync()
    {
        var result = await _dbSet.AsNoTracking().ToListAsync();
        return result;
    }

    public async Task<object> GetManyAsyncInclude(Func<object, bool> func)
    {
        var result = await _dbSet.AsNoTracking().ToListAsync();
        return result.Where(func);
    }
}