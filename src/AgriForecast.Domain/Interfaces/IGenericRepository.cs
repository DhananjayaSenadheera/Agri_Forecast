using System.Linq.Expressions;

namespace AgriForecast.Domain.Interfaces;

public interface IGenericRepository<T> where T : class
{
    Task AddAsync(T entity);
    Task UpdateAsync(T entity);
    Task DeleteAsync(T entity);
    
    Task<T> GetoneAsync();
    Task<T> GetByIdAsync(Guid id);
    Task<T> GetByCodeAsync(string ecoCode);
    Task<T?> GetOneAsyncInclude(Expression<Func<T, bool>> predicate);
    Task<IEnumerable<T>> GetAllAsync();
}