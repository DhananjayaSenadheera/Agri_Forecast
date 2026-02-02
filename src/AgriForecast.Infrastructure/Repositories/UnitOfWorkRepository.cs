using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;

namespace AgriForecast.Infrastructure.Repositories;

public class UnitOfWorkRepository : IUnitofWorkRepository
{
    AgriForecastDbContext _dbContext;
    
    public UnitOfWorkRepository(AgriForecastDbContext dbContext)
    {
        _dbContext = dbContext;
    }
    
    public void Dispose()
    {
       _dbContext.Dispose();
    }

    public async Task CommitAsync()
    {
        await _dbContext.SaveChangesAsync();
    }
}