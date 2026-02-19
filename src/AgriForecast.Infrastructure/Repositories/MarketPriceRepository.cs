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
}