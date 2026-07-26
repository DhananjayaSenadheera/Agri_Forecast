using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Persistence for vintage-aware macro observations. The existence key is the full triple
// (SeriesCode, ReferenceDate, PublishedAt): a revised print of the same period carries a new PublishedAt
// and must be inserted as a distinct row, never skipped as already present.
public interface IMacroSeriesPointRepository
{
    Task AddAsync(MacroSeriesPoint point, CancellationToken ct = default);

    Task<bool> ExistsAsync(string seriesCode, DateTime referenceDate, DateTime publishedAt, CancellationToken ct = default);

    // Vintage prints of a series within a ReferenceDate window, ordered chronologically.
    Task<List<MacroSeriesPoint>> GetRangeAsync(DateTime from, DateTime to, string seriesCode, CancellationToken ct = default);
}
