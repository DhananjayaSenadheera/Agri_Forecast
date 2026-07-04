using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Persistence for vintage-aware macro observations. Shape mirrors IEconomicIndicatorRepository,
// but the existence key is the FULL VINTAGE TRIPLE (SeriesCode, ReferenceDate, PublishedAt): a
// revised print of the same period carries a NEW PublishedAt and MUST be inserted as a distinct
// row, never skipped as "already present". Keying ExistsAsync on only (SeriesCode, ReferenceDate)
// would silently drop revisions — matches the unique index in AgriForecastDbContext.
public interface IMacroSeriesPointRepository
{
    Task AddAsync(MacroSeriesPoint point, CancellationToken ct = default);

    Task<bool> ExistsAsync(string seriesCode, DateTime referenceDate, DateTime publishedAt, CancellationToken ct = default);

    // Read the vintage prints of a series within a ReferenceDate window, ordered chronologically.
    // Mirrors IEconomicIndicatorRepository.GetRangeAsync.
    Task<List<MacroSeriesPoint>> GetRangeAsync(DateTime from, DateTime to, string seriesCode, CancellationToken ct = default);
}
