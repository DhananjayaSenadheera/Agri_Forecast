using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class EconomicIndicatorRepository : IEconomicIndicatorRepository
{
    private readonly AgriForecastDbContext _db;

    public EconomicIndicatorRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(EconomicIndicator indicator, CancellationToken ct = default)
    {
        await _db.EconomicIndicators.AddAsync(indicator, ct);
    }

    public async Task<bool> ExistsAsync(DateTime date, string indicatorCode, CancellationToken ct = default)
    {
        var day = date.Date;
        return await _db.EconomicIndicators
            .AnyAsync(e => e.Date == day && e.IndicatorCode == indicatorCode, ct);
    }

    public async Task<List<EconomicIndicator>> GetRangeAsync(DateTime from, DateTime to, string indicatorCode, CancellationToken ct = default)
    {
        return await _db.EconomicIndicators
            .Where(e => e.IndicatorCode == indicatorCode && e.Date >= from && e.Date <= to)
            .OrderBy(e => e.Date)
            .ToListAsync(ct);
    }
}
