using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class FestivalCalendarRepository : IFestivalCalendarRepository
{
    private readonly AgriForecastDbContext _db;

    public FestivalCalendarRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task<FestivalCalendarEntry> AddAsync(FestivalCalendarEntry entry)
    {
        await _db.FestivalCalendarEntries.AddAsync(entry);
        return entry;
    }

    public Task<FestivalCalendarEntry> UpdateAsync(FestivalCalendarEntry entry)
    {
        _db.FestivalCalendarEntries.Update(entry);
        return Task.FromResult(entry);
    }

    public Task DeleteAsync(FestivalCalendarEntry entry)
    {
        _db.FestivalCalendarEntries.Remove(entry);
        return Task.CompletedTask;
    }

    public async Task<FestivalCalendarEntry?> GetByIdAsync(Guid id)
    {
        return await _db.FestivalCalendarEntries.FindAsync(id);
    }

    public async Task<IEnumerable<FestivalCalendarEntry>> GetAllAsync()
    {
        return await _db.FestivalCalendarEntries
            .AsNoTracking()
            .OrderBy(f => f.Date)
            .ToListAsync();
    }

    public async Task<bool> ExistsAsync(string festivalKey, DateTime date, Guid? excludeId = null)
    {
        var day = date.Date;
        return await _db.FestivalCalendarEntries
            .AsNoTracking()
            .AnyAsync(f => f.FestivalKey == festivalKey
                       && f.Date == day
                       && (excludeId == null || f.Id != excludeId));
    }
}
