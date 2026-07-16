using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Data access for the national festival calendar. Festival rows are as-of-joined into the ML
// model's training features (lead-up demand windows), so this is guarded, curated data — see
// FestivalCalendarController for the Admin-only mutation posture. Mirrors IPolicyFlagRepository.
public interface IFestivalCalendarRepository
{
    Task<FestivalCalendarEntry> AddAsync(FestivalCalendarEntry entry);
    Task<FestivalCalendarEntry> UpdateAsync(FestivalCalendarEntry entry);
    Task DeleteAsync(FestivalCalendarEntry entry);
    Task<FestivalCalendarEntry?> GetByIdAsync(Guid id);

    // All entries, ordered by Date (chronological). The admin Festivals page groups by year.
    Task<IEnumerable<FestivalCalendarEntry>> GetAllAsync();

    // True when another row already occupies this (FestivalKey, Date) slot — mirrors the DB's
    // UNIQUE (FestivalKey, Date) index so a duplicate insert/move fails as a structured 400
    // rather than an unhandled DbUpdateException (500). excludeId skips the row being updated.
    Task<bool> ExistsAsync(string festivalKey, DateTime date, Guid? excludeId = null);
}
