using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class WeatherRecordRepository : IWeatherRecordRepository
{
    private readonly AgriForecastDbContext _db;

    public WeatherRecordRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(WeatherRecord record, CancellationToken ct = default)
    {
        await _db.WeatherRecords.AddAsync(record, ct);
    }

    public async Task<WeatherRecord?> GetByMonthAsync(DateTime month, CancellationToken ct = default)
    {
        return await _db.WeatherRecords.FirstOrDefaultAsync(w => w.Month == month, ct);
    }

    public async Task<List<WeatherRecord>> GetRangeAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        return await _db.WeatherRecords
            .Where(w => w.Month >= from && w.Month <= to)
            .OrderBy(w => w.Month)
            .ToListAsync(ct);
    }
}
