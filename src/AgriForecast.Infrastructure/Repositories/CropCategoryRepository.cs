using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class CropCategoryRepository : ICropCategoryRepository
{
    private readonly AgriForecastDbContext _db;

    public CropCategoryRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct = default)
    {
        return await _db.CropCategories.AsNoTracking().AnyAsync(c => c.Id == id, ct);
    }
}
