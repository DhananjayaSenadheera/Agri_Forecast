using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class MarketPriceRepository: IMarketPriceRepository
{
    private readonly AgriForecastDbContext _db;

    public MarketPriceRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(MarketPrice marketPrice, CancellationToken ct = default)
    {
        await _db.MarketPrices.AddAsync(marketPrice, ct);
    }

    public async Task AddRangeAsync(IEnumerable<MarketPrice> marketPrices, CancellationToken ct = default)
    {
        await _db.MarketPrices.AddRangeAsync(marketPrices, ct);
    }

    public async Task<bool> ExistsAsync(string source, int externalProductId, DateOnly priceDate, CancellationToken ct = default)
    {
        var result = await _db.MarketPrices.AnyAsync(mp =>
            mp.Source == source && mp.ExternalProductId == externalProductId && mp.PriceDate == priceDate, ct);
        return result;
    }

    public async Task<HashSet<DateOnly>> GetExistingDatesAsync(string source, int externalProductId, CancellationToken ct = default)
    {
        var result = await _db.MarketPrices.Where(mp =>
            mp.Source == source && mp.ExternalProductId == externalProductId)
            .Select(mp => mp.PriceDate)
            .ToListAsync(ct);
        return result.ToHashSet();

    }

    public async Task<int> BackfillCropIdAsync(string source, int externalProductId, Guid cropId, CancellationToken ct = default)
    {
        return await _db.MarketPrices
            .Where(mp => mp.Source == source && mp.ExternalProductId == externalProductId && mp.CropId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(mp => mp.CropId, cropId), ct);
    }

    public async Task<List<MarketPrice>> GetByCropIdAsync(Guid cropId, DateOnly from, CancellationToken ct = default)
    {
        return await _db.MarketPrices
            .Where(mp => mp.CropId == cropId && mp.PriceDate >= from)
            .OrderBy(mp => mp.PriceDate)
            .ToListAsync(ct);
    }

    public async Task<List<ExternalProduct>> GetDistinctExternalProductsAsync(string source, CancellationToken ct = default)
    {
        var rows = await _db.MarketPrices
            .Where(mp => mp.Source == source)
            .GroupBy(mp => mp.ExternalProductId)
            .Select(g => new
            {
                ExternalProductId = g.Key,
                Name = g.OrderByDescending(mp => mp.PriceDate)
                        .Select(mp => mp.ExternalProductName)
                        .FirstOrDefault()
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ExternalProduct(r.ExternalProductId, r.Name ?? string.Empty))
            .ToList();
    }
}
