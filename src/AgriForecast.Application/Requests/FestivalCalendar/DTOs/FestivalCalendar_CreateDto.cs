namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

public class FestivalCalendar_CreateDto
{
    // Stable machine key (e.g. "AVURUDU"). The ML feature layer string-matches this against its
    // per-festival columns (InLeadupAvurudu / InLeadupChristmas) case-sensitively, so the
    // validator enforces the UPPERCASE [A-Z0-9_] convention every seed row already follows.
    public string FestivalKey { get; set; }

    // Date-only on the wire; the boundary keeps it date-only in storage too (leakage guard —
    // it is the point-in-time key the ML as-of-joins on and must never carry a hidden time).
    public DateTime Date { get; set; }

    // Pre-festival demand window length, in days. LeadUpDays = 0 is a VALID, first-class value:
    // it is the paired-day convention (a multi-day festival's continuation day carries 0 so the
    // demand window anchored on the eve is not double-counted). Validator allows >= 0, not > 0.
    public int LeadUpDays { get; set; }

    // Date not yet confirmed against the official gazette. Passed straight through — the ML layer
    // may treat provisional rows cautiously; it must never be silently upgraded.
    public bool IsProvisional { get; set; }

    // Gazette citation. REQUIRED on every mutation (unlike PolicyFlag create): festivals are
    // curated training-relevant data, so each row must carry provenance.
    public string? Source { get; set; }
}
