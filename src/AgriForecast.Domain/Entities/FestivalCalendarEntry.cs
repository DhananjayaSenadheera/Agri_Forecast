namespace AgriForecast.Domain.Entities;

// A national festival as a point-in-time row for the ML feature store. Date is date-only so it can
// never carry a hidden time component, and this table is the only source of festival dates — the
// Python feature layer reads it via load_festivals().
public class FestivalCalendarEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable key for the festival family, e.g. "AVURUDU". A string rather than an enum on purpose:
    /// festivals are an open set, so adding one is a seed row, not an enum member plus a migration.
    /// </summary>
    public string FestivalKey { get; set; } = string.Empty;

    // Date-only. Movable festivals get one row per occurrence rather than a recurrence rule.
    public DateTime Date { get; set; }

    // Length of the pre-festival demand window in days: prices are influenced over [Date - LeadUpDays, Date].
    // For a multi-day festival only one row carries the window; the paired day uses 0 to avoid double-counting.
    public int LeadUpDays { get; set; } = 14;

    // True when the date is an estimate not yet confirmed against the gazette. Pass the flag through;
    // never silently upgrade a provisional date.
    public bool IsProvisional { get; set; }

    // Gazette citation, when known.
    public string? Source { get; set; }

    // Record-keeping only; never used as a feature.
    public DateTime CreatedAtUtc { get; set; }
}
