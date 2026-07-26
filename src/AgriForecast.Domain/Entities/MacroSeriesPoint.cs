namespace AgriForecast.Domain.Entities;

// A vintage-aware macroeconomic observation (CCPI, food inflation YoY, ...). A standalone table, not EF
// inheritance from EconomicIndicator, which models a single-date daily reading like USD/LKR.
//
// ReferenceDate is the period the reading describes; PublishedAt is when it became knowable and is what
// the ML layer as-of-joins on. Never default PublishedAt to ReferenceDate — that is lookahead.
public class MacroSeriesPoint
{
    public Guid Id { get; private set; }

    /// <summary>
    /// Series key with the base year embedded, e.g. "CCPI_BASE2021". A string rather than an enum on
    /// purpose: a CBSL rebase must become a new series key, never a silent value shift in an old one.
    /// </summary>
    public string SeriesCode { get; private set; } = null!;

    // The period the reading describes, date-only. The ML as-of-join keys on PublishedAt, not this.
    public DateTime ReferenceDate { get; private set; }

    // Publication vintage, date-only — the key the ML layer as-of-joins on. When the true date cannot be
    // recovered it is imputed LATE (ReferenceDate plus a per-series lag), never set to ReferenceDate.
    public DateTime PublishedAt { get; private set; }

    public decimal Value { get; private set; }
    public string Source { get; private set; } = null!;

    // True when PublishedAt was imputed rather than recovered from the release. Pass the flag through.
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

        // Normalize to date-only before the check so a hidden time component cannot flip it.
        var referenceDay = referenceDate.Date;
        var publishedDay = publishedAt.Date;

        // A figure cannot be published before the period it describes; same-day publication is allowed.
        if (referenceDay > publishedDay)
            throw new ArgumentException(
                "PublishedAt (vintage) cannot precede ReferenceDate — a reading cannot be published before the period it describes",
                nameof(publishedAt));

        // No positive-value guard: YoY and change series can legitimately be zero or negative.

        SeriesCode = seriesCode;
        ReferenceDate = referenceDay;
        PublishedAt = publishedDay;
        Value = value;
        Source = source;
        IsPublishedAtImputed = isPublishedAtImputed;
        RetrievedAtUtc = retrievedAtUtc;
    }
}
