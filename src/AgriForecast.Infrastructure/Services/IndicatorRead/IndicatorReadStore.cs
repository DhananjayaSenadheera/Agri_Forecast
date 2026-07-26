using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.IndicatorRead;

// Read-only projection over EconomicIndicators and MacroSeriesPoints for the admin Indicators page. Pure EF
// LINQ, AsNoTracking — window resolution and validation live in the handlers.
//
// Dates: EconomicIndicator.Date is a DateTime stored at midnight, while the macro dates are SQL "date".
// DateOnly filter bounds are converted to midnight DateTime at the boundary and compared inclusively, then
// materialized DateTimes are coalesced to DateOnly in memory (DateOnly.FromDateTime does not translate).
//
// The macro reads never collapse ReferenceDate and PublishedAt: both flow through verbatim, and the window
// filter is applied on ReferenceDate only.
public class IndicatorReadStore : IIndicatorReadStore
{
    private readonly AgriForecastDbContext _db;

    public IndicatorReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<DateOnly?> GetLatestIndicatorDateAsync(string code, CancellationToken ct = default)
    {
        var q = _db.EconomicIndicators.AsNoTracking().Where(x => x.IndicatorCode == code);
        if (!await q.AnyAsync(ct)) return null;
        var max = await q.MaxAsync(x => x.Date, ct);
        return DateOnly.FromDateTime(max);
    }

    public async Task<IReadOnlyList<IndicatorPointRow>> GetIndicatorRowsAsync(
        string code, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
    {
        var fromDt = fromInclusive.ToDateTime(TimeOnly.MinValue);
        var toDt = toInclusive.ToDateTime(TimeOnly.MinValue);

        var raw = await _db.EconomicIndicators.AsNoTracking()
            .Where(x => x.IndicatorCode == code && x.Date >= fromDt && x.Date <= toDt)
            .OrderBy(x => x.Date)
            .Select(x => new { x.Date, x.IndicatorCode, x.Value, x.Source })
            .ToListAsync(ct);

        return raw
            .Select(x => new IndicatorPointRow(DateOnly.FromDateTime(x.Date), x.IndicatorCode, x.Value, x.Source))
            .ToList();
    }

    public async Task<DateOnly?> GetLatestMacroReferenceDateAsync(string key, CancellationToken ct = default)
    {
        var q = _db.MacroSeriesPoints.AsNoTracking().Where(x => x.SeriesCode == key);
        if (!await q.AnyAsync(ct)) return null;
        var max = await q.MaxAsync(x => x.ReferenceDate, ct);
        return DateOnly.FromDateTime(max);
    }

    public async Task<IReadOnlyList<MacroPointRow>> GetMacroRowsAsync(
        string key, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
    {
        var fromDt = fromInclusive.ToDateTime(TimeOnly.MinValue);
        var toDt = toInclusive.ToDateTime(TimeOnly.MinValue);

        // Window on ReferenceDate only (the chart axis). PublishedAt is projected verbatim and never used to
        // filter — filtering on it would be exactly the leakage collapse this guards against.
        var raw = await _db.MacroSeriesPoints.AsNoTracking()
            .Where(x => x.SeriesCode == key && x.ReferenceDate >= fromDt && x.ReferenceDate <= toDt)
            .OrderBy(x => x.ReferenceDate).ThenBy(x => x.PublishedAt)
            .Select(x => new { x.SeriesCode, x.ReferenceDate, x.PublishedAt, x.Value, x.Source })
            .ToListAsync(ct);

        return raw
            .Select(x => new MacroPointRow(
                x.SeriesCode,
                DateOnly.FromDateTime(x.ReferenceDate),
                DateOnly.FromDateTime(x.PublishedAt),
                x.Value,
                x.Source))
            .ToList();
    }

    public async Task<IReadOnlyList<SeriesCatalogRow>> GetCatalogAsync(CancellationToken ct = default)
    {
        var indicators = await _db.EconomicIndicators.AsNoTracking()
            .GroupBy(x => x.IndicatorCode)
            .Select(g => new { Key = g.Key, Latest = g.Max(x => x.Date), Count = g.Count() })
            .ToListAsync(ct);

        var macro = await _db.MacroSeriesPoints.AsNoTracking()
            .GroupBy(x => x.SeriesCode)
            .Select(g => new { Key = g.Key, Latest = g.Max(x => x.ReferenceDate), Count = g.Count() })
            .ToListAsync(ct);

        var rows = new List<SeriesCatalogRow>();
        rows.AddRange(indicators.Select(x =>
            new SeriesCatalogRow(x.Key, "indicator", DateOnly.FromDateTime(x.Latest), x.Count)));
        rows.AddRange(macro.Select(x =>
            new SeriesCatalogRow(x.Key, "macro", DateOnly.FromDateTime(x.Latest), x.Count)));
        return rows;
    }
}
