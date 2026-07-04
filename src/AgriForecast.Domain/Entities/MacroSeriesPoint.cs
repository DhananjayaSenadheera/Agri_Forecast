namespace AgriForecast.Domain.Entities;

// A single VINTAGE-aware macroeconomic observation (e.g. CCPI headline index, official food
// inflation YoY, food-imports YoY) captured point-in-time for the ML feature store.
//
// STANDALONE table — deliberately NOT EF inheritance from EconomicIndicator: this entity carries
// a second date (PublishedAt / KnowledgeDate = the real publication vintage), which EconomicIndicator
// (a single-date forward-daily reading like USD/LKR) does not model. USD_LKR stays in
// EconomicIndicator (no dual write); macro series with a publish-lag live here.
//
// The two dates are the crux of the leakage discipline:
//   * ReferenceDate — the period the reading DESCRIBES (e.g. "April 2026" → 2026-04-01, date-only).
//   * PublishedAt   — when the figure actually became KNOWABLE (the release/vintage date). The ML
//                     layer as-of-joins on PublishedAt (backward merge_asof) so a value is never
//                     used before it was published. NEVER default PublishedAt := ReferenceDate —
//                     that is anti-conservative (lookahead). See DECISIONS.md 2026-07-04 vintage policy.
public class MacroSeriesPoint
{
    public Guid Id { get; private set; }

    /// <summary>
    /// Stable string key identifying the macro series, with the BASE YEAR embedded in the key
    /// (token form, e.g. "CCPI_BASE2021"). This is a DELIBERATE deviation from the project's local
    /// enum-int convention (cf. PolicyType/PolicyDirection), mirroring FestivalCalendarEntry.FestivalKey:
    /// macro series are an open, provenance-varying set (CBSL rebases the index → a new base year is a
    /// NEW series key, never a silent value shift), so adding one must be a new ingested row — not an
    /// enum member + migration + Python-mirror change. Extensibility beats the enum's compile-time closure.
    /// </summary>
    public string SeriesCode { get; private set; } = null!;

    // The period the reading describes, stored date-only (e.g. a monthly figure → the 1st of its month).
    // Audit/join-key only on the reference axis; the ML as-of-join keys on PublishedAt, not this.
    public DateTime ReferenceDate { get; private set; }

    // The vintage / KnowledgeDate: when the figure became publicly knowable (real publication date).
    // Stored date-only. This is the point-in-time key the ML layer as-of-joins on. When the true
    // publication date could not be recovered it is conservatively imputed LATE (ReferenceDate + a
    // per-series lag prior) and IsPublishedAtImputed is set true — never defaulted to ReferenceDate.
    public DateTime PublishedAt { get; private set; }

    public decimal Value { get; private set; }
    public string Source { get; private set; } = null!;

    // True when PublishedAt was imputed (a lag prior) rather than recovered from the release itself
    // (PDF /CreationDate or listing date). The flag must be passed through, never silently upgraded —
    // the feature layer may treat imputed vintages more cautiously.
    public bool IsPublishedAtImputed { get; private set; }

    public DateTime RetrievedAtUtc { get; private set; }

    private MacroSeriesPoint() { }

    public MacroSeriesPoint(
        string seriesCode,
        DateTime referenceDate,
        DateTime publishedAt,
        decimal value,
        string source,
        bool isPublishedAtImputed,
        DateTime retrievedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(seriesCode))
            throw new ArgumentException("Series code is required", nameof(seriesCode));

        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source is required", nameof(source));

        // Normalize both to date-only BEFORE the invariant check so a hidden time component can
        // never flip the comparison at the day boundary.
        var referenceDay = referenceDate.Date;
        var publishedDay = publishedAt.Date;

        // A vintage cannot precede the period it describes: a figure for April cannot be published
        // before April. (Equality is allowed — same-day publication is legitimate.)
        if (referenceDay > publishedDay)
            throw new ArgumentException(
                "PublishedAt (vintage) cannot precede ReferenceDate — a reading cannot be published before the period it describes",
                nameof(publishedAt));

        // NO positive-Value guard: YoY inflation and change series can legitimately be <= 0
        // (deflation / a falling index), unlike a price or FX rate.

        SeriesCode = seriesCode;
        ReferenceDate = referenceDay;
        PublishedAt = publishedDay;
        Value = value;
        Source = source;
        IsPublishedAtImputed = isPublishedAtImputed;
        RetrievedAtUtc = retrievedAtUtc;
    }
}
