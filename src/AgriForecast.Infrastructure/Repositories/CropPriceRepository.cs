using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class CropPriceRepository : ICropPriceRepository
{
    private readonly AgriForecastDbContext _db;

    public CropPriceRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(CropPrice cropPrice, CancellationToken ct = default)
    {
        await _db.CropPrices.AddAsync(cropPrice, ct);
    }

    public async Task<bool> ExistsAsync(Guid cropId, Guid economicCenterId, DateTime month, CancellationToken ct = default)
    {
        return await _db.CropPrices.AnyAsync(
            cp => cp.CropId == cropId && cp.EconomicCenterId == economicCenterId && cp.Month == month, ct);
    }

    public async Task<List<CropPrice>> GetByCropIdAsync(Guid cropId, CancellationToken ct = default)
    {
        return await _db.CropPrices
            .Where(cp => cp.CropId == cropId)
            .OrderBy(cp => cp.Month)
            .ToListAsync(ct);
    }
}
