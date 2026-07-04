using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class MacroSeriesPointRepository : IMacroSeriesPointRepository
{
    private readonly AgriForecastDbContext _db;

    public MacroSeriesPointRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(MacroSeriesPoint point, CancellationToken ct = default)
    {
        await _db.MacroSeriesPoints.AddAsync(point, ct);
    }

    public async Task<bool> ExistsAsync(string seriesCode, DateTime referenceDate, DateTime publishedAt, CancellationToken ct = default)
    {
        // Key on the FULL vintage triple (matches the unique index): a revised print with a new
        // PublishedAt is a genuinely new row and must NOT be reported as already present.
        var referenceDay = referenceDate.Date;
        var publishedDay = publishedAt.Date;
        return await _db.MacroSeriesPoints
            .AnyAsync(p => p.SeriesCode == seriesCode
                        && p.ReferenceDate == referenceDay
                        && p.PublishedAt == publishedDay, ct);
    }

    public async Task<List<MacroSeriesPoint>> GetRangeAsync(DateTime from, DateTime to, string seriesCode, CancellationToken ct = default)
    {
        return await _db.MacroSeriesPoints
            .Where(p => p.SeriesCode == seriesCode && p.ReferenceDate >= from && p.ReferenceDate <= to)
            .OrderBy(p => p.ReferenceDate)
            .ThenBy(p => p.PublishedAt)
            .ToListAsync(ct);
    }
}
