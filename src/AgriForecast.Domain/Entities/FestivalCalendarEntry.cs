namespace AgriForecast.Domain.Entities;

// A national festival captured as a point-in-time calendar row for the ML feature store.
// Date is the point-in-time key the ML layer as-of-joins on (a festival's price effect is
// felt in the lead-up window [Date - LeadUpDays, Date]); it is stored date-only (no time
// component) so it can never carry a hidden "now" leakage. This table is the single source
// of truth for festival dates — the Python feature layer reads it via load_festivals()
// (mirroring load_policy_flags()); there is no static festival-days twin.
public class FestivalCalendarEntry
{
    public Guid Id { get; set; }

    /// <summary>
    /// Stable string key identifying the festival family, e.g. "AVURUDU", "CHRISTMAS",
    /// "THAI_PONGAL". This is a DELIBERATE deviation from the project's local enum-int
    /// convention (cf. PolicyType/PolicyDirection): festivals are an open set, so adding a
    /// new festival must be a new seed row — never an enum member + migration + Python-mirror
    /// change. Extensibility beats the enum's compile-time closure here.
    /// </summary>
    public string FestivalKey { get; set; } = string.Empty;

    // The festival's calendar date, stored date-only. Movable festivals (Avurudu, Thai Pongal)
    // are stored as one row PER OCCURRENCE (per year) rather than a recurrence rule, so
    // per-year gazette date shifts come for free.
    public DateTime Date { get; set; }

    // Length of the pre-festival demand window, in days: the festival influences prices over
    // [Date - LeadUpDays, Date]. Default 14. For multi-day festival pairs the window is
    // anchored on a single row (LeadUpDays set there) and the paired day carries LeadUpDays=0
    // so the window is not double-counted (see SeedFestivalCalendar for the Avurudu convention).
    public int LeadUpDays { get; set; } = 14;

    // True when the date is a forecast/estimate not yet confirmed against the official gazette
    // (future years, or a lunar festival year we could not cite). The ML layer may treat
    // provisional rows more cautiously; the flag must be passed through, never silently upgraded.
    public bool IsProvisional { get; set; }

    // Provenance: gazette citation string (e.g. Department of Government Printing annual
    // holiday gazette for the year). Nullable.
    public string? Source { get; set; }

    // Audit: when the row was recorded (record-keeping only; NEVER used as a feature).
    public DateTime CreatedAtUtc { get; set; }
}
