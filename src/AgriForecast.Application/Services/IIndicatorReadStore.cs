namespace AgriForecast.Application.Services;

// Read-only projection over EconomicIndicators (single-date daily readings, e.g. USD_LKR)
// and MacroSeriesPoints (VINTAGE-aware macro series, e.g. CCPI). Thin DB seam so the
// GetIndicatorSeries / GetMacroSeries / GetSeriesCatalog handlers are unit-testable with
// canned rows (mirrors IMarketReadStore / IPriceHistoryStore).
//
// LEAKAGE GUARD (load-bearing): a macro row carries TWO dates that MUST reach the wire
// separately — ReferenceDate (the period the figure describes) and PublishedAt (when it
// became knowable). This store NEVER collapses them; MacroPointRow keeps both, and the
// filter window is applied on ReferenceDate (the chart axis), never PublishedAt.
public interface IIndicatorReadStore
{
    // ── EconomicIndicators (daily, single date) ────────────────────────────────────
    // Latest Date for a given IndicatorCode, or null when the series has no rows. This is
    // the default-window anchor (window ends at the latest available reading, not "today",
    // matching the market-overview / price-history anchoring convention).
    Task<DateOnly?> GetLatestIndicatorDateAsync(string code, CancellationToken ct = default);

    // Readings for one IndicatorCode with Date in [fromInclusive, toInclusive], ordered by
    // Date ascending. Empty when the series has no rows in the window.
    Task<IReadOnlyList<IndicatorPointRow>> GetIndicatorRowsAsync(
        string code, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);

    // ── MacroSeriesPoints (vintage-aware, two dates) ───────────────────────────────
    // Latest ReferenceDate for a given SeriesCode, or null when the series has no rows.
    // ReferenceDate (NOT PublishedAt) is the anchor because it is the chart axis.
    Task<DateOnly?> GetLatestMacroReferenceDateAsync(string key, CancellationToken ct = default);

    // Vintage rows for one SeriesCode with ReferenceDate in [fromInclusive, toInclusive],
    // ordered by ReferenceDate then PublishedAt ascending. BOTH dates are returned verbatim;
    // the window is on ReferenceDate only. A revised print of the same period is a distinct
    // row (its own PublishedAt) and is returned too — the caller sees every vintage.
    Task<IReadOnlyList<MacroPointRow>> GetMacroRowsAsync(
        string key, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default);

    // ── Catalog (series picker) ────────────────────────────────────────────────────
    // One row per distinct series across BOTH tables: indicator codes (kind "indicator",
    // latest = max Date) and macro keys (kind "macro", latest = max ReferenceDate), each
    // with its row count. Feeds the admin Indicators series picker (which currently
    // hardcodes CCPI_BASE2021).
    Task<IReadOnlyList<SeriesCatalogRow>> GetCatalogAsync(CancellationToken ct = default);
}

// One EconomicIndicators reading (date coalesced to DateOnly by the store).
public sealed record IndicatorPointRow(DateOnly Date, string IndicatorCode, decimal Value, string Source);

// One MacroSeriesPoints vintage row. ReferenceDate and PublishedAt are BOTH present and
// distinct — never merged.
public sealed record MacroPointRow(
    string SeriesCode, DateOnly ReferenceDate, DateOnly PublishedAt, decimal Value, string Source);

// One catalog entry. Key = IndicatorCode (kind "indicator") or SeriesCode (kind "macro").
// LatestDate = max Date / max ReferenceDate respectively. Count = row count for the series.
public sealed record SeriesCatalogRow(string Key, string Kind, DateOnly LatestDate, int Count);
