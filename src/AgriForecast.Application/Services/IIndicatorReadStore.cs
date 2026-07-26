namespace AgriForecast.Application.Services;

// Read-only projection over EconomicIndicators (daily single-date readings) and MacroSeriesPoints
// (vintage-aware macro series). Thin DB seam so the indicator, macro and catalog handlers are unit-testable.
// A macro row carries TWO dates that must reach the wire separately — ReferenceDate (the period the figure
// describes) and PublishedAt (when it became knowable). This store never collapses them, and the filter
// window applies to ReferenceDate only.
public interface IIndicatorReadStore
{
    // Latest Date for an IndicatorCode, or null when the series has no rows. This is the default-window
    // anchor: the window ends at the latest available reading, not "today".
    Task<DateOnly?> GetLatestIndicatorDateAsync(string code, CancellationToken ct = default);

    // Readings for one IndicatorCode with Date in [fromInclusive, toInclusive], ordered by Date.
    Task<IReadOnlyList<IndicatorPointRow>> GetIndicatorRowsAsync(
        string code, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);

    // Latest ReferenceDate for a SeriesCode, or null when the series has no rows. ReferenceDate, not
    // PublishedAt, because it is the chart axis.
    Task<DateOnly?> GetLatestMacroReferenceDateAsync(string key, CancellationToken ct = default);

    // Vintage rows for one SeriesCode with ReferenceDate in the window, ordered by ReferenceDate then
    // PublishedAt. Both dates come back verbatim and every revised print is its own row.
    Task<IReadOnlyList<MacroPointRow>> GetMacroRowsAsync(
        string key, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);

    // One row per distinct series across both tables, each with its kind, latest date and row count.
    Task<IReadOnlyList<SeriesCatalogRow>> GetCatalogAsync(CancellationToken ct = default);
}

// One EconomicIndicators reading (date coalesced to DateOnly by the store).
public sealed record IndicatorPointRow(DateOnly Date, string IndicatorCode, decimal Value, string Source);

// One MacroSeriesPoints vintage row. ReferenceDate and PublishedAt are both present and never merged.
public sealed record MacroPointRow(
    string SeriesCode, DateOnly ReferenceDate, DateOnly PublishedAt, decimal Value, string Source);

// One catalog entry. Key is the IndicatorCode or SeriesCode; LatestDate is the max Date or ReferenceDate.
public sealed record SeriesCatalogRow(string Key, string Kind, DateOnly LatestDate, int Count);
