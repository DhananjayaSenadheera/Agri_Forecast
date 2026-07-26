using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Data access for the national festival calendar. Festival rows are as-of-joined into ML training
// features, so this is curated, Admin-only-mutated data.
public interface IFestivalCalendarRepository
{
    Task<FestivalCalendarEntry> AddAsync(FestivalCalendarEntry entry);
    Task<FestivalCalendarEntry> UpdateAsync(FestivalCalendarEntry entry);
    Task DeleteAsync(FestivalCalendarEntry entry);
    Task<FestivalCalendarEntry?> GetByIdAsync(Guid id);

    // All entries, ordered by Date (chronological). The admin Festivals page groups by year.
    Task<IEnumerable<FestivalCalendarEntry>> GetAllAsync();

    // Mirrors the DB's UNIQUE (FestivalKey, Date) index so a duplicate insert or move fails as a 400
    // rather than an unhandled DbUpdateException. excludeId skips the row being updated.
    Task<bool> ExistsAsync(string festivalKey, DateTime date, Guid? excludeId = null);
}
